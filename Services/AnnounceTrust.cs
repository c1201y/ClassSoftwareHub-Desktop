namespace ClassSoftwareHub.Desktop.Services;








public sealed class AnnounceSavedPeer
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    
    public long SavedTicks { get; set; }
}









public sealed class AnnounceBinder
{
    public string Code { get; set; } = "";      
    public string Name { get; set; } = "";      
    public string Label { get; set; } = "";     
    public string Ip { get; set; } = "";        
    public long SeenTicks { get; set; }         

    
    
    
    
    
    public string Via { get; set; } = "lan";

    
    public string SenderId { get; set; } = "";
}









public sealed class PendingUnbind
{
    public string Ip { get; set; } = "";
    public string Code { get; set; } = "";      
    public long CreatedTicks { get; set; }
}
