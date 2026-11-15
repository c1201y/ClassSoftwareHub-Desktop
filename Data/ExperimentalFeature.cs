namespace ClassSoftwareHub.Desktop.Data;








public sealed class ExperimentalFeature
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    
    public string Desc { get; init; } = "";

    
    public string Glyph { get; init; } = "";

    
    public string Tag { get; init; } = "";

    
    public override string ToString() => Name;
}
