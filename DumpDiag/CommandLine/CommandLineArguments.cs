namespace DumpDiag.CommandLine
{
    internal static class CommandLineArguments
    {
        internal const string DOTNET_DUMP_PATH_SHORT = "-ddp";
        internal const string DOTNET_DUMP_PATH_LONG = "--dotnet-dump-path";

        internal const string DUMP_FILE_PATH_SHORT = "-dfp";
        internal const string DUMP_FILE_PATH_LONG = "--dump-file-path";

        internal const string DUMP_PROCESS_ID_SHORT = "-dpid";
        internal const string DUMP_PROCESS_ID_LONG = "--dump-process-id";

        internal const string DEGREE_PARALLELISM_SHORT = "-dp";
        internal const string DEGREE_PARALLELISM_LONG = "--degree-parallelism";

        internal const string SAVE_DUMP_FILE_PATH_SHORT = "-sdp";
        internal const string SAVE_DUMP_FILE_PATH_LONG = "--save-dump-file-path";

        internal const string REPORT_FILE_PATH_SHORT = "-rfp";
        internal const string REPORT_FILE_PATH_LONG = "--report-file-path";

        internal const string MIN_COUNT_SHORT = "-mc";
        internal const string MIN_COUNT_LONG = "--min-count";

        internal const string MIN_ASYNC_SIZE_SHORT = "-mas";
        internal const string MIN_ASYNC_SIZE_LONG = "--min-async-size";

        internal const string OVERWRITE_SHORT = "-o";
        internal const string OVERWRITE_LONG = "--overwrite";

        internal const string QUIET_SHORT = "-q";
        internal const string QUIET_LONG = "--quiet";

        internal const string WINDBG_CONNECTION_STRING_SHORT = "-wcs";
        internal const string WINDBG_CONNECTION_STRING_LONG = "--windbg-connection-string";

        internal const string DBGENG_DLL_PATH_SHORT = "-dedp";
        internal const string DBGENG_DLL_PATH_LONG = "--dbgeng-dll-path";

        internal const string RESUMPTION_STATE_FILE_PATH_SHORT = "-rsfp";
        internal const string RESUMPTION_STATE_FILE_PATH_LONG = "--resumption-state-file-path";
    }
}
