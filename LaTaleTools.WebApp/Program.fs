open System
open System.IO
open Giraffe
open LaTaleTools.WebApp
open LaTaleTools.WebApp.AppState
open LaTaleTools.WebApp.Handlers
open LaTaleTools.WebApp.Route
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

let webApp =
    choose [
        route "/" >=> redirectTo false browseBasePath
        subRoute rawBasePath fileDownloadHandler
        subRoute browseBasePath virtualFsHandler
    ]

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let config = builder.Configuration.Get<Config>()

    let binaryPath = config.LaTaleBinaryPath
    if String.IsNullOrEmpty binaryPath || not (Path.Exists binaryPath) then
        invalidArg (nameof config.LaTaleBinaryPath) "Invalid binary path. Does it exist?"

    builder.Services
        .AddLogging()
        .AddGiraffe()
        .AddSingleton<AppState>(
            fun sp ->
                let logger = sp.GetService<ILogger<AppState>>()
                buildAppState logger config.LaTaleBinaryPath
            )
    |> ignore

    use app = builder.Build()
    app.UseGiraffe webApp

    let const' x _ = x
    app.Run() |> const' 0
