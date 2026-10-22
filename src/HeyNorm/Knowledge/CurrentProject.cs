namespace HeyNorm.Knowledge;

/// <summary>
/// The project the person is working on — usually one boat at a time. Questions about "the
/// plans" that don't name a project go to this one. Kept in a one-line file so it survives a
/// restart; the person changes it by saying so ("I'm working on the Heron now").
/// </summary>
public sealed class CurrentProject
{
    private readonly string _path;
    private readonly object _gate = new();
    private string? _name;

    public CurrentProject(string path)
    {
        _path = path;
        try { _name = File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } n ? n : null : null; }
        catch (IOException) { _name = null; }
    }

    public string? Name
    {
        get { lock (_gate) return _name; }
        set
        {
            lock (_gate)
            {
                _name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (_name is null) File.Delete(_path);
                    else File.WriteAllText(_path, _name);
                }
                catch (IOException) { /* still right for this run; it just won't survive a restart */ }
            }
        }
    }
}
