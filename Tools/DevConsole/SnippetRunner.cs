using System;
using System.Reflection;
using System.Text;

namespace SailwindCoop.DevConsole
{
    internal sealed class SnippetResult
    {
        public bool Ok;
        public object Value;
        public string Error;
        public string Output;
    }

    /// <summary>Text a snippet printed during the current request (<c>Dev.Print</c>). Main thread only.</summary>
    internal static class SnippetOutput
    {
        private static readonly StringBuilder Buffer = new StringBuilder();
        public static void Write(string line) { Buffer.Append(line).Append('\n'); }
        public static string Take() { string text = Buffer.ToString(); Buffer.Length = 0; return text; }
    }

    /// <summary>
    /// Runs a snippet that was compiled OUTSIDE the game. The game's mscorlib is the .NET Standard
    /// profile: AssemblyBuilder, TypeBuilder and ILGenerator are stubs that throw, so no in-process C#
    /// compiler can emit code here. <c>devrun.sh</c> compiles the snippet with the SDK compiler against
    /// the game's own assemblies and posts the resulting DLL; this loads it and calls
    /// <c>Snippet.Run()</c>. Loaded assemblies are never unloaded, which is fine for a debugging session.
    /// </summary>
    internal static class SnippetRunner
    {
        public const string TypeName = "Snippet";
        public const string MethodName = "Run";

        public static SnippetResult Run(byte[] assemblyBytes)
        {
            var result = new SnippetResult();
            SnippetOutput.Take();
            try
            {
                var assembly = Assembly.Load(assemblyBytes);
                var type = assembly.GetType(TypeName, false);
                var method = type != null ? type.GetMethod(MethodName, BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null) : null;
                if (method == null) result.Error = "The assembly has no public static " + TypeName + "." + MethodName + "()";
                else { result.Value = method.Invoke(null, null); result.Ok = true; }
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                result.Error = e.ToString();
            }
            result.Output = SnippetOutput.Take();
            return result;
        }
    }
}
