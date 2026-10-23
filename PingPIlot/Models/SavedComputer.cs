namespace PingPilot.Models;

public class SavedComputer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Отображаемое имя, например PC-101.
    public string Name { get; set; } = "";

    // IP-адрес или DNS-имя, которое используется для проверки.
    public string Host { get; set; } = "";

    // Группа, в которой находится компьютер.
    public Guid GroupId { get; set; }

    // Комментарий: назначение, пользователь, расположение и т. п.
    public string Description { get; set; } = "";
}
