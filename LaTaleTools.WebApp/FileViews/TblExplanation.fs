module LaTaleTools.WebApp.FileViews.TblExplanation

open System.IO
open System.Reflection
open Giraffe.ViewEngine

// Only this bundled fragment is rendered without encoding. Keep archive data in
// ordinary Giraffe nodes (str/attributes), not interpolated into this HTML.
let private content =
    lazy
        let resourceName = "LaTaleTools.WebApp.FileViews.TblExplanation.html"
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        if isNull stream then
            invalidOp $"Missing embedded HTML resource: {resourceName}"
        use reader = new StreamReader(stream)
        rawText (reader.ReadToEnd())

let view () : XmlNode = content.Value
