public class ContactViewModel
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? Message { get; set; }

    // NEW: where they came from
    public string? Topic { get; set; }
}

