namespace LaTaleTools.UnitTests

open System
open System.Buffers
open System.IO
open System.IO.Pipelines
open System.Text
open System.Threading
open System.Threading.Tasks
open Giraffe.ViewEngine
open LaTaleTools.WebApp.Handlers
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.VisualStudio.TestTools.UnitTesting

module private StreamingFixture =
    let timeout = TimeSpan.FromSeconds 5.

    let read (pipe: Pipe) = task {
        let! result = pipe.Reader.ReadAsync().AsTask().WaitAsync(timeout)
        let text = Encoding.UTF8.GetString(result.Buffer.ToArray())
        pipe.Reader.AdvanceTo(result.Buffer.End)
        return text, result.IsCompleted
    }

    let assertCanceled (operation: Task) = task {
        try
            do! operation.WaitAsync(timeout)
            Assert.Fail("Expected cancellation.")
        with :? OperationCanceledException ->
            Assert.IsTrue(operation.IsCanceled)
    }

    let render (isHtml: bool) node = task {
        use stream = new MemoryStream()
        let writer = PipeWriter.Create(stream, StreamPipeWriterOptions(leaveOpen = true))
        try
            let write = if isHtml then AsyncViewWriter.writeHtmlAsync else AsyncViewWriter.writeXml
            do! (write writer node).WaitAsync(timeout)
            return Encoding.UTF8.GetString(stream.ToArray())
        finally
            writer.Complete()
    }

[<TestClass>]
type StreamingHtmlTests() =
    [<TestMethod>]
    member _.FlushesSmallResponseAndLeavesPipeOwnedByCaller(): Task = task {
        let pipe = Pipe()
        try
            do! AsyncViewWriter.writeHtmlAsync pipe.Writer (p [] [str "hello"])
            let! text, completed = StreamingFixture.read pipe
            Assert.AreEqual("<p>hello</p>", text)
            Assert.IsFalse(completed)
            // Rendering must not complete or dispose the response pipe.
            let! _ = pipe.Writer.WriteAsync(ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes "tail"))
            let! tail, _ = StreamingFixture.read pipe
            Assert.AreEqual("tail", tail)
        finally
            pipe.Reader.Complete()
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    [<DataRow(false)>]
    [<DataRow(true)>]
    member _.HandlerFlushesPrefixBeforeAsyncNodeAndObservesRequestCancellation(cancel: bool): Task = task {
        let pipe = Pipe()
        use cancellation = new CancellationTokenSource()
        let pending = TaskCompletionSource<XmlNode>(TaskCreationOptions.RunContinuationsAsynchronously)
        let ctx = DefaultHttpContext()
        ctx.RequestAborted <- cancellation.Token
        ctx.Features.Set<IHttpResponseBodyFeature>(
            { new IHttpResponseBodyFeature with
                member _.Stream = Stream.Null
                member _.Writer = pipe.Writer
                member _.DisableBuffering() = ()
                member _.StartAsync(_) = Task.CompletedTask
                member _.SendFileAsync(_, _, _, _) = Task.CompletedTask
                member _.CompleteAsync() = failwith "The handler must not complete the response pipe." })
        try
            let node = div [] [str "prefix"; asyncNode pending.Task; str "suffix"]
            let request = streamingHtmlView node (fun _ -> failwith "Unexpected next handler.") ctx
            let! prefix, completed = StreamingFixture.read pipe
            Assert.AreEqual("<div>prefix", prefix)
            Assert.AreEqual("text/html; charset=utf-8", ctx.Response.ContentType)
            Assert.IsFalse(completed)
            Assert.IsFalse(request.IsCompleted)
            if cancel then
                cancellation.Cancel()
                do! StreamingFixture.assertCanceled request
                Assert.IsFalse(pending.Task.IsCompleted, "Cancellation must stop waiting for an unfinished node.")
            else
                pending.SetResult(str "middle")
                let! result = request.WaitAsync(StreamingFixture.timeout)
                Assert.IsTrue(result.IsSome)
                let! suffix, _ = StreamingFixture.read pipe
                Assert.AreEqual("middlesuffix</div>", suffix)
        finally
            pending.TrySetResult(emptyText) |> ignore
            pipe.Reader.Complete()
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    member _.LargeSynchronousNodeFlushesInBoundedChunksAndWaitsForReader(): Task = task {
        let pipe = Pipe(PipeOptions(pauseWriterThreshold = 1024L, resumeWriterThreshold = 512L))
        // Put a surrogate pair across an encoding chunk boundary, including inside a large attribute.
        let text = String('x', 4095) + "😀漢字" + String('y', 200000)
        let node = div [KeyValue("data-value", text)] [rawText text; br []]
        let expected = RenderView.AsString.htmlNode node
        try
            let writing = AsyncViewWriter.writeHtmlAsync pipe.Writer node
            Assert.IsFalse(writing.IsCompleted, "The writer must await backpressure before finishing the tree.")
            let finish = task {
                try
                    do! writing
                finally
                    pipe.Writer.Complete()
            }
            let mutable completed = false
            let mutable reads = 0
            // Decode only once all bytes are collected: pipe segments may split UTF-8 sequences.
            use bytes = new MemoryStream()
            while not completed do
                let! result = pipe.Reader.ReadAsync().AsTask().WaitAsync(StreamingFixture.timeout)
                Assert.IsTrue(result.Buffer.Length <= 16L * 1024L, "A flush must bound queued output.")
                let chunk = result.Buffer.ToArray()
                bytes.Write(chunk, 0, chunk.Length)
                completed <- result.IsCompleted
                reads <- reads + 1
                pipe.Reader.AdvanceTo(result.Buffer.End)
            do! finish.WaitAsync(StreamingFixture.timeout)
            Assert.IsTrue(reads > 2)
            Assert.AreEqual(expected, Encoding.UTF8.GetString(bytes.ToArray()))
        finally
            pipe.Reader.Complete()
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    member _.CancellationInterruptsBackpressuredFlush(): Task = task {
        let pipe = Pipe(PipeOptions(pauseWriterThreshold = 1L, resumeWriterThreshold = 1L))
        use cancellation = new CancellationTokenSource()
        try
            let writing = AsyncViewWriter.writeHtmlAsyncWithCancellation pipe.Writer cancellation.Token (str "blocked")
            Assert.IsFalse(writing.IsCompleted)
            cancellation.Cancel()
            do! StreamingFixture.assertCanceled writing
        finally
            pipe.Reader.Complete()
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    member _.CanceledFlushResultStopsRendering(): Task = task {
        let pipe = Pipe()
        let pending = TaskCompletionSource<XmlNode>()
        try
            pipe.Writer.CancelPendingFlush()
            let writing = AsyncViewWriter.writeHtmlAsync pipe.Writer (div [] [str "prefix"; asyncNode pending.Task])
            do! StreamingFixture.assertCanceled writing
        finally
            pending.TrySetResult(emptyText) |> ignore
            pipe.Reader.Complete()
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    member _.CompletedReaderStopsBeforeWaitingForMoreNodes(): Task = task {
        let pipe = Pipe()
        let pending = TaskCompletionSource<XmlNode>()
        try
            pipe.Reader.Complete()
            let writing = AsyncViewWriter.writeHtmlAsync pipe.Writer (div [] [str "prefix"; asyncNode pending.Task])
            do! writing.WaitAsync(StreamingFixture.timeout)
            Assert.IsFalse(pending.Task.IsCompleted)
        finally
            pending.TrySetResult(emptyText) |> ignore
            pipe.Writer.Complete()
    }

    [<TestMethod>]
    [<DataRow(false)>]
    [<DataRow(true)>]
    member _.PreservesHtmlAndXmlRendering(isHtml: bool): Task = task {
        let node = div [_title "a & b\""; flag "hidden"] [
            comment "example"
            str "<encoded>"
            asyncNode (Task.FromResult(p [] [rawText "plain 😀"; br []]))
        ]
        let expected =
            if isHtml then RenderView.AsString.htmlNode node
            else RenderView.AsString.xmlNode node
        let! actual = StreamingFixture.render isHtml node
        Assert.AreEqual(expected, actual)
    }

    [<TestMethod>]
    member _.DebugStringWriterHandlesMoreThanPipePauseThreshold(): Task = task {
        let text = String('x', 200000)
        let! actual = (AsyncViewWriter.debugWriteToString (str text)).WaitAsync(StreamingFixture.timeout)
        Assert.AreEqual(text, actual)
    }
