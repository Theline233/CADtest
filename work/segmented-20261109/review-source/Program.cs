using System.Security.Cryptography;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
Console.InputEncoding = new System.Text.UTF8Encoding(false);
try
{
    // Do not open the dataset or workbook until the parent has installed its Job.
    if(args.Length>0 && args[^1]=="--wait-for-go")
    {
        if(Console.ReadLine()!="GO")throw new InvalidOperationException("invalid_gate");
        args=args[..^1];
    }
    else throw new ArgumentException("guard gate is required");
    if(args.Length==0)throw new ArgumentException("Commands: prepare --input dataset.json --output new-folder; validate/export --manifest folder/source.json --review reviewed.xlsx [--output new-folder --reviewer name]");
    var parameters=new Dictionary<string,string>(StringComparer.Ordinal);
    for(int i=1;i<args.Length;i+=2){if(i+1>=args.Length||!args[i].StartsWith("--")||!parameters.TryAdd(args[i][2..],args[i+1]))throw new ArgumentException("Invalid or repeated argument");}
    string Need(string name)=>parameters.TryGetValue(name,out var value)?value:throw new ArgumentException("Missing --"+name);
    if(args[0]=="prepare")
    {
        ReviewWorkbooks.Prepare(Need("input"),Need("output"));
    }
    else if(args[0] is "validate" or "export")
    {
        var result=args[0]=="export"
            ?ReviewWorkbooks.Export(Need("manifest"),Need("review"),Need("output"),Need("reviewer"))
            :ReviewWorkbooks.Validate(Need("manifest"),Need("review"));
        Console.WriteLine(JsonData.Text(result));
        Environment.ExitCode=result.Status is "ready" or "nothing_to_export"?0:2;
    }
    else throw new ArgumentException("Unknown command");
}
catch(Exception ex)
{
    Console.Error.WriteLine(JsonData.Text(new {status="failed",error=ex.Message,errorType=ex.GetType().Name}));
    Environment.ExitCode=1;
}
