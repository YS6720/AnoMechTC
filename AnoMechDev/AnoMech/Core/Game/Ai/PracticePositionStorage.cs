using System;
using System.IO;

namespace AnoMech.Core.Game.Ai;

/// <summary>持久化 seam：純 runtime 不直接依賴 Dalamud，失敗可回報給位置編輯器。</summary>
public interface IPracticePositionStore
{
    bool TryLoad(out string contents, out string? error);
    bool TrySave(string contents, out string? error);
}

/// <summary>
/// 以單一 JSON 檔保存練習覆寫。寫入採 temp + replace，避免 UI 途中留下半份檔案。
/// pathProvider 延後到第一次 Begin／Save 才呼叫，讓 SimWorld 建構不依賴 plugin service 初始化順序。
/// </summary>
public sealed class FilePracticePositionStore : IPracticePositionStore
{
    private readonly Func<string> pathProvider;

    public FilePracticePositionStore(Func<string> pathProvider)
    {
        this.pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    public bool TryLoad(out string contents, out string? error)
    {
        contents = string.Empty;
        error = null;
        try
        {
            var path = pathProvider();
            contents = File.ReadAllText(path);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TrySave(string contents, out string? error)
    {
        error = null;
        string? temporary = null;
        try
        {
            var path = pathProvider();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch { /* original save error is the useful UI message */ }
            }
        }
    }
}
