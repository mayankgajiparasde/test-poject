using HotelUnderwriting.Api;

if (args is ["dev-token", .. var rest]) return DevToken.Run(rest, Console.Out, Console.Error);
if (args is [WorkbookWorker.Command, var limit]) return WorkbookWorker.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), limit); // the child process of an Excel upload
var app = ApiHost.Build(args);
app.Run();
return 0;
