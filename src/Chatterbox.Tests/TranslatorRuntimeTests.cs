using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The truth about which translation build runs: read off LLamaSharp's own
// loader log, never off which pack happens to be installed; and the tool
// LLamaSharp needs to see a Vulkan GPU at all.
public class TranslatorRuntimeTests : IDisposable
{
    public void Dispose() => LlamaTranslator.ResetForTests();

    [Theory]
    [InlineData(@"C:\Users\u\Desktop\Chatterbox\runtimes\win-x64\native\vulkan\llama.dll", "vulkan")]
    [InlineData(@"C:\Users\u\Desktop\Chatterbox\runtimes\win-x64\native\avx2\llama.dll", "avx2")]
    [InlineData("runtimes/win-x64/native/noavx/llama.dll", "noavx")]
    [InlineData(@"C:\somewhere\llama.dll", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheVariantIsTheFolderUnderNative(string? path, string expected) =>
        Assert.Equal(expected, LlamaTranslator.ParseVariant(path));

    [Fact]
    public void TheLoadedLibraryComesFromTheSuccessfullyLoadedLine()
    {
        LlamaTranslator.ResetForTests();
        var seen = new List<string>();
        void Collect(string l) => seen.Add(l);
        LlamaTranslator.OnLoaderLog += Collect;
        try
        {
            LlamaTranslator.NoteLoaderLine("Detected OS Platform: 'WINDOWS'");
            LlamaTranslator.NoteLoaderLine(@"Got relative library path 'runtimes\win-x64\native\vulkan\llama.dll' from local with {...}, trying to load it...");
            LlamaTranslator.NoteLoaderLine(@"Successfully loaded dependency 'C:\x\runtimes\win-x64\native\avx2\ggml-cpu.dll'");
            LlamaTranslator.NoteLoaderLine("Successfully loaded 'C:\\x\\runtimes\\win-x64\\native\\vulkan\\llama.dll'\r\n");
            Assert.Equal(@"C:\x\runtimes\win-x64\native\vulkan\llama.dll", LlamaTranslator.LoadedLibrary);
            Assert.Equal("vulkan", LlamaTranslator.LoadedVariant);
            // Only load/fail lines reach the boot log, not the OS chatter.
            Assert.Equal(2, seen.Count);
            Assert.All(seen, l => Assert.Contains("loaded", l));
        }
        finally { LlamaTranslator.OnLoaderLog -= Collect; }
    }

    [Fact]
    public void FindToolLooksWhereWindowsStartsAProgramByName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        // A name nothing else on this machine has: the search also covers
        // the app folder, System32 and the Windows folder.
        var name = "chatterbox-test-tool-" + Guid.NewGuid().ToString("N") + ".exe";
        try
        {
            var missing = Path.Combine(dir, "nonexistent");
            Assert.Null(LlamaTranslator.FindTool(name, missing + Path.PathSeparator + bin));
            var tool = Path.Combine(bin, name);
            File.WriteAllText(tool, "");
            Assert.Equal(tool, LlamaTranslator.FindTool(name, missing + Path.PathSeparator + bin));
            Assert.Equal(tool, LlamaTranslator.FindTool(name, "\"" + bin + "\""));   // a quoted PATH entry
            Assert.Null(LlamaTranslator.FindTool(name, missing));
            Assert.Null(LlamaTranslator.FindTool(name, ""));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void TheExeCarriesBothWhisperBuilds()
    {
        // The AVX2 build and the no-AVX build (the voice detector loads
        // through whisper.cpp, so an older CPU needs the latter even for
        // Parakeet). Each is the same four libraries.
        var names = typeof(LlamaTranslator).Assembly.GetManifestResourceNames();
        foreach (var lib in new[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" })
        {
            Assert.Contains("natives/win-x64/" + lib, names);
            Assert.Contains("natives/noavx/win-x64/" + lib, names);
        }
        Assert.EndsWith(Path.Combine("runtimes", "noavx", "win-x64"), SttPaths.NoAvxNativeDir);
        Assert.EndsWith(Path.Combine("runtimes", "win-x64"), SttPaths.NativeDir);
    }
}
