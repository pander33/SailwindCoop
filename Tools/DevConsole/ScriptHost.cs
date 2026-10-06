using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Mono.CSharp;

namespace SailwindCoop.DevConsole
{
    internal sealed class ScriptResult
    {
        public bool Ok;
        public bool HasValue;
        public object Value;
        public string Error;
        public string Output;
    }

    /// <summary>Text a script printed during the current request (<c>Dev.Print</c>). Main thread only.</summary>
    internal static class ScriptOutput
    {
        private static readonly StringBuilder Buffer = new StringBuilder();
        public static void Write(string line) { Buffer.Append(line).Append('\n'); }
        public static string Take() { string text = Buffer.ToString(); Buffer.Length = 0; return text; }
    }

    /// <summary>
    /// A persistent C# session on top of <c>Mono.CSharp.Evaluator</c>: variables and usings declared by
    /// one request stay for the next until <see cref="Reset"/>. Every assembly loaded into the process
    /// is referenced, including ones that load after the first request.
    /// </summary>
    internal sealed class ScriptHost
    {
        private readonly string[] _usings;
        private readonly HashSet<Assembly> _referenced = new HashSet<Assembly>();
        private readonly List<string> _pendingUsings = new List<string>();
        private Evaluator _evaluator;
        private StringWriter _errors;
        private ReportPrinter _printer;

        public ScriptHost(params string[] usings) { _usings = usings; }

        public void Reset() { _evaluator = null; }

        public ScriptResult Run(string code)
        {
            var result = new ScriptResult();
            ScriptOutput.Take();
            try
            {
                Prepare();
                object value;
                bool hasValue;
                string rest = _evaluator.Evaluate(code, out value, out hasValue);
                if (_printer.ErrorsCount > 0) result.Error = _errors.ToString().Trim();
                else if (rest != null) result.Error = "Incomplete input: " + rest;
                else { result.Ok = true; result.HasValue = hasValue; result.Value = value; }
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                string compiler = _errors != null ? _errors.ToString().Trim() : "";
                result.Error = compiler.Length != 0 ? compiler : e.ToString();
            }
            result.Output = ScriptOutput.Take();
            return result;
        }

        private void Prepare()
        {
            if (_evaluator == null)
            {
                _errors = new StringWriter();
                _printer = new StreamReportPrinter(_errors);
                // Default references are resolved by name (System.Xml and others); a stripped game
                // does not have them all. Everything that IS loaded gets referenced below instead.
                var settings = new CompilerSettings { LoadDefaultReferences = false };
                _evaluator = new Evaluator(new CompilerContext(settings, _printer));
                _referenced.Clear();
                _pendingUsings.Clear();
                _pendingUsings.AddRange(_usings);
            }

            bool added = false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == typeof(object).Assembly || _referenced.Contains(assembly)) continue;
                _referenced.Add(assembly);
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                    _evaluator.ReferenceAssembly(assembly);
                    added = true;
                }
                catch (Exception) { }
            }

            // A using for a namespace nobody has loaded yet is an error; keep it for a later request.
            if (added)
            {
                for (int i = _pendingUsings.Count - 1; i >= 0; i--)
                {
                    Clear();
                    bool ok;
                    try { ok = _evaluator.Run("using " + _pendingUsings[i] + ";") && _printer.ErrorsCount == 0; }
                    catch (Exception) { ok = false; }
                    if (ok) _pendingUsings.RemoveAt(i);
                }
            }
            Clear();
        }

        private void Clear()
        {
            _errors.GetStringBuilder().Length = 0;
            _printer.Reset();
        }
    }
}
