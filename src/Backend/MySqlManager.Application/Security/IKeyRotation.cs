namespace MySqlManager.Application.Security;

public interface IKeyRotation
{
    Task<string> RotateAsync(CancellationToken cancellationToken);
}
