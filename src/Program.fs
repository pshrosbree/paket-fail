open System
open System.IO
open System.Net
open System.Threading

open Argu

type CLIArguments =
    | [<ExactlyOnce; AltCommandLine("-r")>] Remote of remote:string
    | [<ExactlyOnce; AltCommandLine("-l")>] Local of local:string 
    | [<Unique; AltCommandLine("-c")>] ChunkSize of chunkSize:int
with
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Remote _ -> "address to proxy, ending with '/'"
            | Local _ -> "local listener, ending with '/'"
            | ChunkSize _ -> "number of bytes to transfer at a time"

type System.Net.HttpListener with
    member x.AsyncGetContext() = Async.FromBeginEnd(x.BeginGetContext, x.EndGetContext)

type System.Net.WebClient with
    member x.AsyncDownloadData(uri) = 
        Async.FromContinuations(fun (success, error, cancel) ->
        x.DownloadDataCompleted.Add(fun result ->
            match isNull result.Error, result.Cancelled with
            | true, _ -> error result.Error
            | _, true -> cancel (new OperationCanceledException())
            | _ -> success result.Result)
        x.DownloadDataAsync(uri) )

type System.Net.HttpListener with 
    static member Start(url, worker) = 
        let tokenSource = new CancellationTokenSource()
        Async.Start( 
            async { 
                use listener = new HttpListener()
                listener.Prefixes.Add(url)
                listener.Start()
                while true do 
                    let! context = listener.AsyncGetContext()
                    Async.Start(worker context, tokenSource.Token)
            },
            cancellationToken = tokenSource.Token)
        tokenSource

    static member StartSynchronous(url, worker) =
        HttpListener.Start(url, worker >> async.Return) 

let getProxyUrl url (context:HttpListenerContext) = Uri(url + context.Request.Url.PathAndQuery)

let asyncHandleError (context:HttpListenerContext) (e:exn) = async {
    use wr = new StreamWriter(context.Response.OutputStream)
    wr.Write("<h1>Request Failed</h1>")
    wr.Write("<p>" + e.Message + "</p>")
    context.Response.Close() }

let asyncHandleRequest chunkSize url context = async {
    let request = HttpWebRequest.Create(getProxyUrl url context)
    use! response = request.AsyncGetResponse()
    use stream = response.GetResponseStream()
    context.Response.SendChunked <- true

    let count = ref 1
    let buffer = Array.zeroCreate chunkSize
    while count.Value > 0 do
        let! read = stream.AsyncRead(buffer, 0, buffer.Length)
        do! context.Response.OutputStream.AsyncWrite(buffer, 0, read)    
        count := read
    context.Response.Close() }

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CLIArguments>(programName = "SimpleHttpProxy.exe")
    try
        let results = parser.Parse argv
        let remote = results.GetResult Remote
        let local = results.GetResult Local
        let chunkSize = results.GetResult (ChunkSize, defaultValue = 4096)
        printfn "Proxying %s on %s with a chunk size of %d" remote local chunkSize
        let token = HttpListener.Start(local, asyncHandleRequest chunkSize remote)
        printfn "Press enter to terminate..."
        token.Cancel()
        Console.ReadLine() |> ignore
        0
    with
    | :? ArguParseException -> 
        parser.PrintUsage() |> eprintfn "%s"
        -1
    | ex ->
        eprintfn "%s" ex.Message
        -2
