using System.Text;
using CadMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

System.Console.InputEncoding = new UTF8Encoding(false);
System.Console.OutputEncoding = new UTF8Encoding(false);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<CadGateway>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<OfficialTools>();
await builder.Build().RunAsync();
