module LaTaleTools.Library.Tbl

open System
open System.IO
open System.Text
open LaTaleTools.Library.Spf
open LaTaleTools.Library.Util
open SkiaSharp

type Sprite =
    { /// Frame lookup key, not the record's position or animation playback order.
      FrameNumber: int32
      /// Placement of the crop relative to the original frame's top-left.
      Offset: Vec2<int32>
      /// Cropped top-left relative to the original frame's center. Negate this
      /// vector to obtain the rotation pivot in cropped-image coordinates.
      CenterOffset: Vec2<single>

      PosTopLeft: Vec2<int32>
      PosBottomRight: Vec2<int32>
      File: ArchivePath
    }

type SpriteGroup =
    { Name: string
      /// Declared total, stored separately from the serialized record count.
      TotalFrames: int32
      Sprites: Sprite list
    }

let public readSpriteStream (sprite: Sprite) (baseFile: byte ReadOnlySpan) : byte array =
    let (Vec2(width, height)) = sprite.PosBottomRight - sprite.PosTopLeft
    use sourceImage = SKBitmap.Decode(baseFile)

    use outputBitMap = new SKBitmap(width, height)
    let outputRect = SKRectI.Create(0, 0, width, height)
    use canvas = new SKCanvas(outputBitMap)
    canvas.Clear()

    let (Vec2(x, y)) = sprite.PosTopLeft
    let cropRect = SKRectI.Create(x, y, width, height)
    canvas.DrawBitmap(sourceImage, cropRect, outputRect, SKSamplingOptions(SKFilterMode.Nearest))

    use outputImage = SKImage.FromBitmap(outputBitMap)
    use imageData = outputImage.Encode(SKEncodedImageFormat.Png, 100)
    imageData.ToArray()

let public readSpriteGroups (tblBaseDirPath: string) (view: UnmanagedMemoryAccessor): SpriteGroup list =
    let requireBytes pos size =
        if pos < 0L || size < 0L || pos > view.Capacity || size > view.Capacity - pos then
            raise (InvalidDataException($"Truncated TBL at byte {pos}: need {size} bytes."))

    let readCount pos label =
        let count = view.ReadInt32 pos
        if count < 0 then
            raise (InvalidDataException($"Negative TBL {label} at byte {pos}."))
        count

    requireBytes 0L 8L
    let version = view.ReadInt32 0L
    if version <> 100 then
        raise (InvalidDataException($"Unsupported TBL version {version}; expected 100."))
    let groupCount = readCount 4L "group count"
    requireBytes 8L (136L * int64 groupCount)

    let readSprites pos count =
        let spriteSize = 164L
        requireBytes pos (spriteSize * int64 count)
        let readSingleSprite n =
            { FrameNumber = view.ReadInt32 (pos + spriteSize * n)
              Offset = view.ReadVec2<int32> (pos + spriteSize * n + 4L)
              CenterOffset = view.ReadVec2<single> (pos + spriteSize * n + 12L)
              PosTopLeft = view.ReadVec2<int32> (pos + spriteSize * n + 20L)
              PosBottomRight = view.ReadVec2<int32> (pos + spriteSize * n + 28L)
              File =
                  let file = view.ReadNullTerminatedString (pos + spriteSize * n + 36L) 128 Encoding.ASCII
                  ArchivePath.Join(tblBaseDirPath, file.ToUpperInvariant())
            }
        let sprites =
            [ 0L .. int64 count - 1L ]
            |> List.map readSingleSprite
        (pos + spriteSize * int64(count), sprites)
    let endPos, revGroups =
        let groupSize = 136L
        let foldFunc (pos, revGroups) _ =
            requireBytes pos groupSize
            let totalFrames = readCount pos "total frame count"
            let name = view.ReadNullTerminatedString (pos + 4L) 128 Encoding.ASCII
            let spriteCount = readCount (pos + 132L) "stored frame count"

            let newPos, sprites = readSprites (pos + groupSize) spriteCount
            let newGroup = { Name = name; TotalFrames = totalFrames; Sprites = sprites }
            (newPos, newGroup :: revGroups)
        List.fold foldFunc (8L, []) [ 0 .. groupCount - 1 ]

    if endPos <> view.Capacity then
        raise (InvalidDataException($"Unexpected trailing TBL data at byte {endPos}."))
    List.rev revGroups
