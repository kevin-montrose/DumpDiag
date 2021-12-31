using System.Runtime.Versioning;

namespace DumpDiag.CommandLine
{
    internal enum ExitCodes: int
    {
        Success = 0,

        CouldNotParseParameters = -1,
        
        BothDumpAndProcessSpecified = -2,
        SaveDumpFileMustHaveDumpProcessId = -3,
        DegreeParallelismTooLow = -4,
        MinCountTooLow = -5,
        MinAsyncSizeTooLow = -6,
        CouldNotFindDotNetDump = -7,
        ReportFileExists = -8,
        DumpFileExists = -9,
        DumpFileDirectoryError = -10,
        DumpFailed = -11,
        CouldNotFindDumpFile = -12,

        [SupportedOSPlatform("windows")]
        DbgEngDllPathNotSet = -13,
        
        [SupportedOSPlatform("windows")]
        WindbgConnectionStringNotSet = -14,
        [SupportedOSPlatform("windows")]
        WindbgConnectionStringMissingPort = -15,
        [SupportedOSPlatform("windows")]
        WindbgConnectionStringBadPort = -16,
        [SupportedOSPlatform("windows")]
        WindbgConnectionStringBadIP = -17,
        [SupportedOSPlatform("windows")]
        DbgEngDllNotFound = -18,
    }
}
