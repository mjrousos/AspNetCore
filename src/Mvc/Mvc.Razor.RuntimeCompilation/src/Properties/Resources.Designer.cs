// <auto-generated />
namespace Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation
{
    using System.Globalization;
    using System.Reflection;
    using System.Resources;

    internal static class Resources
    {
        private static readonly ResourceManager _resourceManager
            = new ResourceManager("Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation.Resources", typeof(Resources).GetTypeInfo().Assembly);

        /// <summary>
        /// One or more compilation failures occurred:
        /// </summary>
        internal static string CompilationFailed
        {
            get => GetString("CompilationFailed");
        }

        /// <summary>
        /// One or more compilation failures occurred:
        /// </summary>
        internal static string FormatCompilationFailed()
            => GetString("CompilationFailed");

        /// <summary>
        /// One or more compilation references may be missing. If you're seeing this in a published application, set '{0}' to true in your project file to ensure files in the refs directory are published.
        /// </summary>
        internal static string Compilation_MissingReferences
        {
            get => GetString("Compilation_MissingReferences");
        }

        /// <summary>
        /// One or more compilation references may be missing. If you're seeing this in a published application, set '{0}' to true in your project file to ensure files in the refs directory are published.
        /// </summary>
        internal static string FormatCompilation_MissingReferences(object p0)
            => string.Format(CultureInfo.CurrentCulture, GetString("Compilation_MissingReferences"), p0);

        /// <summary>
        /// '{0}.{1}' must not be empty. At least one '{2}' is required to locate a view for rendering.
        /// </summary>
        internal static string FileProvidersAreRequired
        {
            get => GetString("FileProvidersAreRequired");
        }

        /// <summary>
        /// '{0}.{1}' must not be empty. At least one '{2}' is required to locate a view for rendering.
        /// </summary>
        internal static string FormatFileProvidersAreRequired(object p0, object p1, object p2)
            => string.Format(CultureInfo.CurrentCulture, GetString("FileProvidersAreRequired"), p0, p1, p2);

        /// <summary>
        /// Generated Code
        /// </summary>
        internal static string GeneratedCodeFileName
        {
            get => GetString("GeneratedCodeFileName");
        }

        /// <summary>
        /// Generated Code
        /// </summary>
        internal static string FormatGeneratedCodeFileName()
            => GetString("GeneratedCodeFileName");

        /// <summary>
        /// The debug type specified in the dependency context could be parsed. The debug type value '{0}' is not supported.
        /// </summary>
        internal static string UnsupportedDebugInformationFormat
        {
            get => GetString("UnsupportedDebugInformationFormat");
        }

        /// <summary>
        /// The debug type specified in the dependency context could be parsed. The debug type value '{0}' is not supported.
        /// </summary>
        internal static string FormatUnsupportedDebugInformationFormat(object p0)
            => string.Format(CultureInfo.CurrentCulture, GetString("UnsupportedDebugInformationFormat"), p0);

        private static string GetString(string name, params string[] formatterNames)
        {
            var value = _resourceManager.GetString(name);

            System.Diagnostics.Debug.Assert(value != null);

            if (formatterNames != null)
            {
                for (var i = 0; i < formatterNames.Length; i++)
                {
                    value = value.Replace("{" + formatterNames[i] + "}", "{" + i + "}");
                }
            }

            return value;
        }
    }
}
