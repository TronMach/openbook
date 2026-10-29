using OpenBook.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

return await new CliApp(Console.Out, Console.Error, CliSettings.DefaultPath).RunAsync(args, cancel.Token);
