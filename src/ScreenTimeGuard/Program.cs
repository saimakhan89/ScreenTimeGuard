using ScreenTimeGuard;

var host = Host.CreateDefaultBuilder(args)
    // Sets ContentRoot to AppContext.BaseDirectory so appsettings.json is found
    // when the service runs from C:\Windows\System32.
    .UseContentRoot(AppContext.BaseDirectory)
    .UseWindowsService(options =>
    {
        options.ServiceName = "ScreenTimeGuard";
    })
    .ConfigureServices((ctx, services) =>
    {
        // Bind the "BlockerConfig" section; IOptionsMonitor picks up file changes live.
        services.Configure<BlockerConfig>(ctx.Configuration.GetSection("BlockerConfig"));
        services.AddHostedService<BlockerWorker>();
    })
    .Build();

await host.RunAsync();
