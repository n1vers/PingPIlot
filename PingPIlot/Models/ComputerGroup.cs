namespace PingPilot.Models;

public class ComputerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";
}
