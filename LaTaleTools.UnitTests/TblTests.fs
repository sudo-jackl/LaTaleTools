namespace LaTaleTools.UnitTests

open System
open System.IO
open System.IO.MemoryMappedFiles
open System.Text
open LaTaleTools.Library.Spf
open LaTaleTools.Library.Tbl
open LaTaleTools.Library.Util
open Microsoft.VisualStudio.TestTools.UnitTesting

module private TblFixture =
    let create () =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.ASCII, true)
        let name (text: string) =
            let bytes = Array.create 128 0xCCuy // Bytes after the terminator are not necessarily zero.
            Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0)
            bytes[text.Length] <- 0uy
            writer.Write bytes
        writer.Write 100
        writer.Write 2
        writer.Write 20 // Declared total must not control how many records are read.
        name "02_0210_118_02.png"
        writer.Write 2
        for frameNumber in [12; 3] do // Preserve file order, not numeric order.
            writer.Write frameNumber
            writer.Write -5
            writer.Write 7
            writer.Write -21.5f
            writer.Write -9.5f
            for coordinate in [10; 20; 36; 47] do writer.Write coordinate
            name "Long_Atlas_Filename001.png"
        writer.Write 9
        name "empty"
        writer.Write 0
        writer.Flush()
        stream.ToArray()

    let read (bytes: byte array) =
        use map = MemoryMappedFile.CreateNew(null, int64 bytes.Length)
        use view = map.CreateViewAccessor(0L, int64 bytes.Length)
        view.WriteArray(0L, bytes, 0, bytes.Length)
        readSpriteGroups "DATA/TEST" view

    let setInt (offset: int) value (bytes: byte array) =
        BitConverter.GetBytes(value: int).CopyTo(bytes, offset)
        bytes

[<TestClass>]
type TblTests() =
    [<TestMethod>]
    member _.ReadsFullNamesSeparateCountsAndPlacementFields() =
        let groups = TblFixture.create() |> TblFixture.read
        Assert.AreEqual(2, groups.Length)
        let group = groups[0]
        Assert.AreEqual("02_0210_118_02.png", group.Name)
        Assert.AreEqual(20, group.TotalFrames)
        Assert.AreEqual<int list>([12; 3], group.Sprites |> List.map _.FrameNumber)
        let sprite = group.Sprites[0]
        Assert.AreEqual(Vec2(-5, 7), sprite.Offset)
        Assert.AreEqual(Vec2(-21.5f, -9.5f), sprite.CenterOffset)
        Assert.AreEqual(Vec2(10, 20), sprite.PosTopLeft)
        Assert.AreEqual(Vec2(36, 47), sprite.PosBottomRight)
        Assert.AreEqual(ArchivePath "DATA/TEST/LONG_ATLAS_FILENAME001.PNG", sprite.File)
        Assert.AreEqual("empty", groups[1].Name)
        Assert.AreEqual(9, groups[1].TotalFrames)
        Assert.IsTrue(groups[1].Sprites.IsEmpty)

    [<TestMethod>]
    member _.AllowsAnEmptyTable() =
        let bytes = Array.append (BitConverter.GetBytes 100) (BitConverter.GetBytes 0)
        Assert.IsTrue((TblFixture.read bytes).IsEmpty)

    [<TestMethod>]
    member _.RejectsUnsupportedVersion() =
        let bytes = TblFixture.create() |> TblFixture.setInt 0 101
        Assert.ThrowsExactly<InvalidDataException>(fun () -> TblFixture.read bytes |> ignore) |> ignore

    [<TestMethod>]
    [<DataRow(4)>]
    [<DataRow(8)>]
    [<DataRow(140)>]
    member _.RejectsNegativeCounts(offset: int) =
        let bytes = TblFixture.create() |> TblFixture.setInt offset -1
        Assert.ThrowsExactly<InvalidDataException>(fun () -> TblFixture.read bytes |> ignore) |> ignore

    [<TestMethod>]
    [<DataRow(4)>]
    [<DataRow(140)>]
    member _.RejectsCountsLargerThanTheInput(offset: int) =
        let bytes = TblFixture.create() |> TblFixture.setInt offset Int32.MaxValue
        Assert.ThrowsExactly<InvalidDataException>(fun () -> TblFixture.read bytes |> ignore) |> ignore

    [<TestMethod>]
    [<DataRow(4)>]
    [<DataRow(143)>]
    [<DataRow(300)>]
    [<DataRow(607)>]
    member _.RejectsTruncatedHeadersAndRecords(length: int) =
        let bytes = TblFixture.create() |> Array.take length
        Assert.ThrowsExactly<InvalidDataException>(fun () -> TblFixture.read bytes |> ignore) |> ignore

    [<TestMethod>]
    member _.RejectsUnexplainedTrailingData() =
        let bytes = Array.append (TblFixture.create()) [|0uy|]
        Assert.ThrowsExactly<InvalidDataException>(fun () -> TblFixture.read bytes |> ignore) |> ignore
