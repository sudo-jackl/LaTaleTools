namespace LaTaleTools.UnitTests

open System
open System.IO
open System.IO.MemoryMappedFiles
open System.Text
open System.Threading
open System.Threading.Tasks
open Giraffe
open LaTaleTools.Library.Ldt
open LaTaleTools.Library.VirtualFs
open LaTaleTools.WebApp.AppState
open LaTaleTools.WebApp.Handlers
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.VisualStudio.TestTools.UnitTesting

module private ResourceFixture =
    let services () =
        ServiceCollection()
            .AddSingleton<AppState>(fun _ ->
                { VirtualFs = { FileMapping = Map.empty; Root = buildTree ["DATA/TEST.LDT"] }
                  SpfViews = Map.empty })
            .BuildServiceProvider()

    let context services =
        let ctx = DefaultHttpContext()
        ctx.RequestServices <- services
        ctx.Request.Path <- PathString "/root/DATA/TEST.LDT"
        ctx

    let next: HttpFunc = fun ctx -> Task.FromResult(Some ctx)

    let archive source entry =
        let map = MemoryMappedFile.CreateNew(null, 281L)
        use view = map.CreateViewAccessor()
        // One payload byte, one 140-byte directory record, then the trailer.
        let path = Encoding.ASCII.GetBytes(entry: string)
        view.WriteArray(1L, path, 0, path.Length)
        view.Write(133L, 1)
        { PhysicalPath = source; MemoryMappedFile = map }

    let assertClosed handles =
        for handle in handles do
            Assert.IsTrue(handle.MemoryMappedFile.SafeMemoryMappedFileHandle.IsClosed)

[<TestClass>]
type ResourceLifetimeTests() =
    [<TestMethod>]
    [<DataRow("success")>]
    [<DataRow("skip")>]
    [<DataRow("failure")>]
    [<DataRow("cancellation")>]
    member _.RequestOwnsStreamUntilHandlerCompletes(outcome: string): Task = task {
        use services = ResourceFixture.services ()
        use stream = new MemoryStream([|42uy|])
        use cancellation = new CancellationTokenSource()
        let ctx = ResourceFixture.context services
        ctx.RequestAborted <- cancellation.Token
        let completion = TaskCompletionSource<HttpContext option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let handler _ (resource: Stream): HttpHandler = fun _ _ -> task {
            let! result = completion.Task
            Assert.AreEqual(42, resource.ReadByte())
            return result
        }
        let request = genericFileHandler (fun _ _ -> Some (stream :> Stream)) handler ResourceFixture.next ctx
        Assert.IsFalse(request.IsCompleted)
        Assert.IsTrue(stream.CanRead, "The resource must survive the handler's asynchronous work.")

        match outcome with
        | "failure" -> completion.SetException(InvalidDataException "preview failed")
        | "cancellation" ->
            cancellation.Cancel()
            completion.SetCanceled(cancellation.Token)
        | "skip" -> completion.SetResult(None)
        | _ -> completion.SetResult(Some ctx)

        try
            let! result = request
            Assert.IsTrue(outcome = "success" || outcome = "skip")
            Assert.AreEqual((outcome = "success"), result.IsSome)
        with
        | :? InvalidDataException as ex ->
            Assert.AreEqual("failure", outcome)
            Assert.AreEqual("preview failed", ex.Message)
        | :? OperationCanceledException ->
            Assert.AreEqual("cancellation", outcome)
            Assert.IsTrue(request.IsCanceled)

        Assert.IsFalse(stream.CanRead, "Completion, failure, and cancellation must all release the stream.")
    }

    [<TestMethod>]
    member _.DisposesStreamWhenHandlerConstructionThrows(): Task = task {
        use services = ResourceFixture.services ()
        use stream = new MemoryStream()
        let handler _ (_: Stream): HttpHandler = raise (InvalidDataException "invalid preview")
        let request =
            genericFileHandler (fun _ _ -> Some (stream :> Stream)) handler
                ResourceFixture.next (ResourceFixture.context services)
        try
            let! _ = request
            Assert.Fail("Expected a preview failure.")
        with :? InvalidDataException -> ()
        Assert.IsFalse(stream.CanRead)
    }

    [<TestMethod>]
    member _.KeepsAccessorAliveForLazyLdtRowsAfterAwait(): Task = task {
        use services = ResourceFixture.services ()
        use map = MemoryMappedFile.CreateNew(null, 8724L)
        use view = map.CreateViewAccessor()
        view.Write(8L, 2) // The second row is read lazily; only the implicit row ID column is present.
        view.Write(8716L, 42)
        view.Write(8720L, 43)
        let completion = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let handler _ accessor: HttpHandler =
            let table = readLdtTable accessor
            fun _ ctx -> task {
                do! completion.Task
                Assert.AreEqual<Cell list list>([[SignedCell 42]; [SignedCell 43]], List.ofSeq table.Rows)
                return Some ctx
            }
        let request =
            genericFileHandler (fun _ _ -> Some view) handler
                ResourceFixture.next (ResourceFixture.context services)
        Assert.IsFalse(request.IsCompleted)
        Assert.IsFalse(view.SafeMemoryMappedViewHandle.IsClosed)
        completion.SetResult()
        let! _ = request
        Assert.IsTrue(view.SafeMemoryMappedViewHandle.IsClosed)
    }

    [<TestMethod>]
    member _.ServiceProviderDisposesApplicationMaps() =
        let handles =
            [ResourceFixture.archive "one.spf" "DATA/A.XML"
             ResourceFixture.archive "two.spf" "DATA/B.XML"]
        try
            use services =
                ServiceCollection()
                    .AddSingleton<AppState>(fun _ ->
                        buildAppStateFromFiles NullLogger.Instance (fun path -> handles |> List.find (fun h -> h.PhysicalPath = path))
                            ["one.spf"; "two.spf"])
                    .BuildServiceProvider()
            let state = services.GetRequiredService<AppState>()
            use stream = openStreamInArchive "DATA/A.XML" state |> Option.get
            Assert.AreEqual(0, stream.ReadByte())
            Assert.IsFalse(handles[0].MemoryMappedFile.SafeMemoryMappedFileHandle.IsClosed)
            services.Dispose()
            ResourceFixture.assertClosed handles
            // Host cleanup and repeated disposal should be safe.
            (state :> IDisposable).Dispose()
        finally
            for handle in handles do handle.MemoryMappedFile.Dispose()

    [<TestMethod>]
    member _.DisposesEarlierMapsWhenOpeningAnArchiveFails() =
        let handle = ResourceFixture.archive "one.spf" "DATA/A.XML"
        use map = handle.MemoryMappedFile
        let openArchive path =
            if path = "one.spf" then handle
            else raise (IOException "open failed")
        let ex = Assert.ThrowsExactly<IOException>(fun () ->
            buildAppStateFromFiles NullLogger.Instance openArchive ["one.spf"; "two.spf"] |> ignore)
        Assert.AreEqual("open failed", ex.Message)
        ResourceFixture.assertClosed [handle]

    [<TestMethod>]
    member _.DisposesAllMapsWhenMetadataParsingFails() =
        let good = ResourceFixture.archive "one.spf" "DATA/A.XML"
        use goodMap = good.MemoryMappedFile
        use badMap = MemoryMappedFile.CreateNew(null, 1L)
        let bad = { PhysicalPath = "two.spf"; MemoryMappedFile = badMap }
        let openArchive path = if path = "one.spf" then good else bad
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(fun () ->
            buildAppStateFromFiles NullLogger.Instance openArchive ["one.spf"; "two.spf"] |> ignore) |> ignore
        ResourceFixture.assertClosed [good; bad]

    [<TestMethod>]
    member _.DisposesAllMapsWhenFilesystemConstructionFails() =
        // Conflicting file/directory paths fail during tree construction, after metadata parsing.
        let first = ResourceFixture.archive "one.spf" "DATA/A"
        let second = ResourceFixture.archive "two.spf" "DATA/A/B.XML"
        use firstMap = first.MemoryMappedFile
        use secondMap = second.MemoryMappedFile
        let openArchive path = if path = "one.spf" then first else second
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            buildAppStateFromFiles NullLogger.Instance openArchive ["one.spf"; "two.spf"] |> ignore) |> ignore
        ResourceFixture.assertClosed [first; second]
