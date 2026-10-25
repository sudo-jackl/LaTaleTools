// Build the solution in Debug first. These references use the app's resolved dependencies.
#r "../LaTaleTools.WebApp/bin/Debug/net10.0/FSharpPlus.dll"
#r "../LaTaleTools.WebApp/bin/Debug/net10.0/SkiaSharp.dll"
#r "../LaTaleTools.WebApp/bin/Debug/net10.0/LaTaleTools.Library.dll"

open System
open System.IO
open System.IO.MemoryMappedFiles
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Buffers.Binary
open LaTaleTools.Library
open LaTaleTools.Library.Spf
open LaTaleTools.Library.Util

let check condition message = if not condition then failwith message
let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length < 1 || args.Length > 2 then
    failwith "Usage: dotnet fsi scripts/SmokeTest.fsx <SPF-directory> [http://localhost:5126]"

let directory = Path.GetFullPath args[0]
let archives =
    Directory.GetFiles directory
    |> Array.filter (fun p -> Path.GetExtension(p).Equals(".spf", StringComparison.OrdinalIgnoreCase))
    |> Array.sort
check (archives.Length > 0) $"No SPF files in {directory}"

let withArchive path action =
    use mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read)
    action mmf

let entries =
    archives
    |> Array.collect (fun archive ->
        withArchive archive (fun mmf ->
            use view = mmf.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read)
            let metadata = readMetadata view
            let directoryStart = view.Capacity - 140L * int64(metadata.Length + 1)
            for entry in metadata do
                check (entry.ContentPosition >= 0 && entry.ContentSize >= 0 &&
                       int64 entry.ContentPosition + int64 entry.ContentSize <= directoryStart)
                      $"Out-of-bounds entry: {archive}: {entry.Path}"
            printfn "SPF %s: %d entries" (Path.GetFileName archive) metadata.Length
            metadata |> List.map (fun e -> archive, e) |> List.toArray))

let files = entries |> Array.map (fun (a, e) -> e.Path, (a, e)) |> Map.ofArray
check (files.Count = entries.Length) "Duplicate virtual paths: select one archive set before testing."

let withEntryView archive (entry: FileEntryMetadata) action =
    withArchive archive (fun mmf ->
        use view = mmf.CreateViewAccessor(int64 entry.ContentPosition, int64 entry.ContentSize, MemoryMappedFileAccess.Read)
        action view)

let readBytes path =
    let archive, entry = files[ArchivePath path]
    use stream = File.OpenRead archive
    stream.Position <- int64 entry.ContentPosition
    let data = Array.zeroCreate<byte> entry.ContentSize
    stream.ReadExactly data
    data

let mutable tables, rows, tbls, groups, sprites = 0, 0, 0, 0, 0
let mutable missingImages = Set.empty<ArchivePath>
let imageSizes = System.Collections.Generic.Dictionary<ArchivePath, int * int>()
let mutable outOfBoundsCrops = 0
let imageSize path =
    match imageSizes.TryGetValue path with
    | true, size -> size
    | _ ->
        let archive, entry = files[path]
        check (entry.ContentSize >= 24) $"Truncated PNG header: {path}"
        use stream = File.OpenRead archive
        stream.Position <- int64 entry.ContentPosition
        let header = Array.zeroCreate<byte> 24
        stream.ReadExactly header
        check (header[0..7] = [|137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy|])
              $"Expected PNG image reference: {path}"
        let size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
                   BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4))
        imageSizes.Add(path, size)
        size

for archive, entry in entries do
    let (ArchivePath path) = entry.Path
    match Path.GetExtension(path).ToUpperInvariant() with
    | ".LDT" ->
        withEntryView archive entry (fun view ->
            let table = Ldt.readLdtTable view
            // Rows are lazy: consume them before disposing the view.
            let mutable count = 0
            for row in table.Rows do
                check (row.Length = table.Columns.Length) $"Wrong cell count in {path}"
                count <- count + 1
            check (count = view.ReadInt32 8L) $"Wrong row count in {path}"
            tables <- tables + 1
            rows <- rows + count)
    | ".TBL" ->
        withEntryView archive entry (fun view ->
            let parent = path.Substring(0, path.LastIndexOf('/'))
            let parsed = Tbl.readSpriteGroups parent view
            check (parsed.Length = view.ReadInt32 4L) $"Wrong group count in {path}"
            let count = parsed |> List.sumBy (fun g -> g.Sprites.Length)
            check (8L + int64 parsed.Length * 136L + int64 count * 164L = view.Capacity)
                  $"TBL records do not consume the whole entry: {path}"
            for group in parsed do
                for sprite in group.Sprites do
                    let (Vec2(left, top)), (Vec2(right, bottom)) = sprite.PosTopLeft, sprite.PosBottomRight
                    let (Vec2(cx, cy)) = sprite.CenterOffset
                    check (right > left && bottom > top && Single.IsFinite cx && Single.IsFinite cy)
                          $"Invalid TBL rectangle or center offset: {path}, {group.Name}, {sprite.FrameNumber}"
                    if not (files.ContainsKey sprite.File) then
                        missingImages <- missingImages.Add sprite.File
                    else
                        let width, height = imageSize sprite.File
                        if left < 0 || top < 0 || right > width || bottom > height then
                            outOfBoundsCrops <- outOfBoundsCrops + 1
            tbls <- tbls + 1
            groups <- groups + parsed.Length
            sprites <- sprites + count)
    | _ -> ()

printfn "PASS: %d archives, %d entries; %d LDTs / %d rows; %d TBLs / %d groups / %d sprites"
    archives.Length entries.Length tables rows tbls groups sprites
printfn "INFO: %d distinct missing TBL image references (reported, not treated as parser failures)" missingImages.Count
printfn "INFO: %d TBL crops exceed PNG bounds across %d available referenced images (header checks, not full decodes)"
    outOfBoundsCrops imageSizes.Count

if args.Length = 2 then
    // These examples belong to the supplied top-level Chinese client archive set.
    // HTTP checks do not prove browser playback/autoplay, layout, or all image crops.
    use handler = new HttpClientHandler(AllowAutoRedirect = false)
    use client = new HttpClient(handler, BaseAddress = Uri(args[1].TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds 60.)
    let get (path: string) = client.GetAsync(path).GetAwaiter().GetResult()
    let text (response: HttpResponseMessage) = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    let route prefix (path: string) =
        prefix + "/" + (path.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/")
    use redirect = get ""
    check (redirect.StatusCode = HttpStatusCode.Found && string redirect.Headers.Location = "/root") "Root redirect failed"
    use root = get "root"
    check (root.IsSuccessStatusCode && (text root).Contains("DATA")) "Directory listing failed"
    for path in ["root/DATA/__LAT_TOOLS_MISSING__"; "raw/DATA/__LAT_TOOLS_MISSING__"] do
        use response = get path
        check (response.StatusCode = HttpStatusCode.NotFound) $"Expected 404: {path}"

    let samples = [
        "DATA/LDT/WORLD_MAP_STAGE.LDT", "_RowId"
        "DATA/CHAR/MONSTER/006_TRAP/TRAP2.TBL", "data:image/png;base64,"
        "DATA/CHAR/MONSTER/006_TRAP/TRAP041.PNG", "<img"
        "DATA/INTERFACE/UI/UI_AUCTION_POPUP_WAIT.XML", "&lt;UI&gt;"
        "DATA/BGM/TITLEBGM.MP3", "<audio"
        "DATA/SOUND/[UI]_01_GEM.WAV", "<audio"
        "DATA/CHAR/MONSTER/MONSTER.MFT", "Download"
    ]
    let koreanPath =
        entries |> Array.pick (fun (_, e) ->
            let (ArchivePath path) = e.Path
            if path |> Seq.exists (fun c -> c >= '\uAC00' && c <= '\uD7A3') then Some path else None)
    for path, marker in samples @ [koreanPath, "Download"] do
        check (files.ContainsKey (ArchivePath path)) $"Smoke-test sample absent: {path}"
        use page = get (route "root" path)
        let html = text page
        check (page.IsSuccessStatusCode && html.Contains marker) $"Preview failed: {path} ({page.StatusCode})"
        if path.EndsWith("TRAP2.TBL") then
            check (html.Contains("Frame number") && html.Contains("Center offset") &&
                   html.Contains("Image missing from loaded archives")) "TBL field labels or missing-image message absent"
            let crops = Regex.Matches(html, "data:image/png;base64,([^\"]+)")
            // TRAP040.PNG is one of the known missing references in this sample set.
            check (crops.Count = 1) "Expected one available trap sprite"
            let dimensions =
                [for crop in crops do
                    let png = Convert.FromBase64String(crop.Groups[1].Value)
                    check (png[0..7] = [|137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy|]) "Invalid cropped PNG"
                    yield BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
                          BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4))]
            check (dimensions = [(1024, 116)]) "Unexpected crop dimensions"
        use raw = get (route "raw" path)
        check raw.IsSuccessStatusCode $"Download failed: {path}"
        if path = koreanPath then
            check (raw.Content.Headers.ContentDisposition.FileNameStar = Path.GetFileName path)
                  "Korean download filename was not preserved in filename*"
        let actual = raw.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        check (actual = readBytes path) $"Downloaded bytes differ from SPF payload: {path}"
        printfn "HTTP PASS: preview + exact download: %s" path
    printfn "PASS: HTTP redirect, directory, 404s, previews, crop dimensions, and downloads"
