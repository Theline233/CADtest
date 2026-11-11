using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

public sealed record SourceFile(string Path,string Sha256);
public sealed record ReviewFile(string Id,string Name,string Path,string Sha256,string Diagnostics,bool RequiresAcknowledgement);
public sealed record CadValues(string DrawingNumber,string? Name,string? Material,string? Treatment,decimal? Quantity,string? Unit);
public sealed record DefaultDecision(string Mode,string Note);
// Both additions are optional: old datasets lack them. Duplicate is evidence,
// not an approval or a validation rule. Retain the JSON without assuming every
// optional member is present or that later producers cannot add new members.
// DefaultDecision only pre-fills the confirmation area; validation still applies.
public sealed record ReviewItem(string Id,string FileId,CadValues Cad,string Warnings,bool RequiresReview,string Kind,string[] Parents,string QuantityEvidence,string TreatmentEvidence,string ReferenceInfo,DefaultDecision? DefaultDecision=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JsonElement? Duplicate=null);
public sealed record ReviewDataset(int SchemaVersion,string TaskId,string Title,SourceFile[] Sources,ReviewFile[] Files,ReviewItem[] Items);
public sealed record FileDecision(string FileId,string State,string Note,string ParentChildNote);
public sealed record AcceptedItem(string Id,string FileId,string Mode,CadValues Original,CadValues Final,string Reason,string[] Parents);
public sealed record ExcludedItem(string Id,string FileId,string DrawingNumber,string Reason);
public sealed class ValidationResult
{
    public string Status {get;set;}="blocked";
    public string TaskId {get;set;}="";
    public string ManifestSha256 {get;set;}="";
    public string ReviewSha256 {get;set;}="";
    public List<string> Errors {get;}=[];
    public List<AcceptedItem> Accepted {get;}=[];
    public List<ExcludedItem> Excluded {get;}=[];
    public List<FileDecision> Files {get;}=[];
    public int Pending {get;set;}
}
public static class JsonData
{
    public static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,PropertyNameCaseInsensitive=true,WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public static T Read<T>(string path)=>JsonSerializer.Deserialize<T>(File.ReadAllText(path),Options)??throw new InvalidDataException("Empty JSON");
    public static string Text<T>(T value)=>JsonSerializer.Serialize(value,Options);
}
