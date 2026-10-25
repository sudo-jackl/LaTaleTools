module LaTaleTools.WebApp.FileViews.TblView

open System
open System.Threading.Tasks
open FSharpPlus
open Giraffe.ViewEngine
open LaTaleTools.Library.Spf
open LaTaleTools.Library.Tbl
open LaTaleTools.WebApp.Views

let public tblView (groups: SpriteGroup list) (getImageBase64: Sprite -> string option Task): XmlNode list =
    let titleRow =
        tr [] [
            th [] [ str "TBL Group Name" ]
            th [] [ str "Image Path" ]
            th [] [ str "Frame number" ]
            th [] [ str "Drawing offset" ]
            th [] [ str "Center offset" ]
            th [] [ str "Atlas top-left" ]
            th [] [ str "Size" ]
            th [] [ str "Preview" ]
        ]
    let renderSpriteRow sprite =
        let (ArchivePath ap) = sprite.File
        let spritePreview = task {
            let! base64Img = getImageBase64 sprite

            let preview =
                match base64Img with
                | None -> [ str "Image missing from loaded archives" ]
                | Some base64Img ->
                    [ img [ _src $"data:image/png;base64,{base64Img}"; _alt $"Frame {sprite.FrameNumber}" ] ]
            return td [] preview
        }
        [
            td [] (pathView true ap ap String.Empty)
            td [] [ str (sprite.FrameNumber.ToString()) ]
            td [] [ str (sprite.Offset.ToString()) ]
            td [] [ str (sprite.CenterOffset.ToString()) ]
            td [] [ str (sprite.PosTopLeft.ToString()) ]
            td [] [ str ((sprite.PosBottomRight - sprite.PosTopLeft).ToString()) ]
            asyncNode spritePreview
        ]
        
    let attachGroupInfo (group: SpriteGroup) rows =
        let groupRow =
            td [
                _rowspan ((max 1 group.Sprites.Length).ToString())
            ] [
                str $"{group.Name} ({group.Sprites.Length} stored, {group.TotalFrames} declared)"
            ]
        match rows with
        | head :: tail -> ((groupRow :: head) :: tail)
        | _ -> [ groupRow
                 :: List.singleton (td [ _colspan "7" ] [ i [] [ str "<empty>" ] ])
               ]
    let sprites =
        [
            for group in groups ->
                List.map renderSpriteRow group.Sprites
                |> attachGroupInfo group
                |> List.map (tr [])
        ]

    let rows = List.collect id sprites
    [
        LaTaleTools.WebApp.FileViews.TblExplanation.view ()
        hr []
        table [ _border "1" ] (titleRow :: rows)
    ]
