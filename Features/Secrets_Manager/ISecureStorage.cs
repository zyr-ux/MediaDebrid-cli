using System.Threading.Tasks;

namespace MediaDebrid_cli.Features.Secrets_Manager;

public interface ISecureStorage
{
    Task SaveAsync(string key, string value);
    Task<string?> LoadAsync(string key);
    Task DeleteAsync(string key);
}