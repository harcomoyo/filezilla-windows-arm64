using FileZilla.Bridge;

return await BridgeApp.RunAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error,
    new NativeClient());
