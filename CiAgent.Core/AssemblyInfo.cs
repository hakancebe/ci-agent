using System.Runtime.CompilerServices;

// ReportService'in internal static (BuildCommentBody, BuildMarker, FindByMarker, ...)
// yardımcı metodlarını CiAgent.Tests'ten test edebilmek için. Java'daki package-private
// erişimin C#'taki en yakın karşılığı: "internal" + InternalsVisibleTo.
[assembly: InternalsVisibleTo("CiAgent.Tests")]
