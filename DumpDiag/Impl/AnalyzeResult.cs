using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal readonly struct AnalyzeResult
    {
        internal ImmutableDictionary<NameWithSize, ReferenceStats> TypeReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> StringReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> DelegateReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> CharacterArrayReferenceStats { get; }
        internal ImmutableDictionary<NameWithSize, ReferenceStats> AsyncStateMatchineStats { get; }
        internal ThreadAnalysis ThreadDetails { get; }

        internal AnalyzeResult(
            ImmutableDictionary<NameWithSize, ReferenceStats> typeReferenceStats,
            ImmutableDictionary<string, ReferenceStats> stringReferenceStats,
            ImmutableDictionary<string, ReferenceStats> delegateReferenceStats,
            ImmutableDictionary<string, ReferenceStats> characterArrayReferenceStats,
            ImmutableDictionary<NameWithSize, ReferenceStats> asyncStateMatchineStats,
            ThreadAnalysis threadDetails)
        {
            TypeReferenceStats = typeReferenceStats;
            StringReferenceStats = stringReferenceStats;
            DelegateReferenceStats = delegateReferenceStats;
            CharacterArrayReferenceStats = characterArrayReferenceStats;
            AsyncStateMatchineStats = asyncStateMatchineStats;
            ThreadDetails = threadDetails;
        }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                var task = WriteToAsync(writer, 1);

                task.GetAwaiter().GetResult();

                return writer.ToString();
            }
        }

        internal async ValueTask WriteToAsync(TextWriter writer, int minCount)
        {
            var builder = new StringBuilder();

            await WriteSegmentWithSizeAsync(writer, "Types", TypeReferenceStats, minCount, builder).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteSegmentAsync(writer, "Delegates", DelegateReferenceStats, minCount, builder).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteSegmentAsync(writer, "Strings", StringReferenceStats, minCount, builder).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteSegmentAsync(writer, "Char[]", CharacterArrayReferenceStats, minCount, builder).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteSegmentWithSizeAsync(writer, "Async State Machines", AsyncStateMatchineStats, minCount, builder).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteUniqueStackFramesAsync(writer, ThreadDetails).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteCallStacksAsync(writer, ThreadDetails).ConfigureAwait(false);

            static async ValueTask WriteCallStacksAsync(TextWriter writer, ThreadAnalysis threadDetails)
            {
                await writer.WriteLineAsync("Call Stacks").ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', "Call Stacks".Length)).ConfigureAwait(false);

                for (var threadIx = 0; threadIx < threadDetails.ThreadStacks.Count; threadIx++)
                {
                    var stack = threadDetails.ThreadStacks[threadIx];
                    var header = $"Thread #{threadIx}: {stack.Count:N0} frames";

                    await writer.WriteLineAsync(header).ConfigureAwait(false);
                    await writer.WriteLineAsync(new string('-', header.Length)).ConfigureAwait(false);

                    foreach (var frame in stack)
                    {
                        await writer.WriteLineAsync(frame.CallSite).ConfigureAwait(false);
                    }

                    await writer.WriteLineAsync().ConfigureAwait(false);
                }
            }

            static async ValueTask WriteUniqueStackFramesAsync(TextWriter writer, ThreadAnalysis threadDetails)
            {
                await writer.WriteLineAsync("Unique Stack Frames").ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', "Unique Stack Frames".Length)).ConfigureAwait(false);

                var maxCount = threadDetails.StackFrameCounts.Max(x => x.Value);
                var countSize = maxCount.ToString("N0").Length;
                countSize = Math.Max("Count".Length, countSize);

                await WritePartAsync(writer, "Count", countSize).ConfigureAwait(false);
                await writer.WriteAsync("   Call Site").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                await writer.WriteLineAsync(new string('-', countSize + 3 + "Call Site".Length)).ConfigureAwait(false);

                foreach (var kv in threadDetails.StackFrameCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                {
                    await WritePartAsync(writer, kv.Value.ToString("N0"), countSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await writer.WriteAsync(kv.Key).ConfigureAwait(false);
                    await writer.WriteLineAsync();
                }
            }

            static async ValueTask WriteSegmentWithSizeAsync(
                TextWriter writer,
                string header,
                ImmutableDictionary<NameWithSize, ReferenceStats> rawDict,
                int minCount,
                StringBuilder builder
            )
            {
                var dict = rawDict.Where(kv => kv.Value.Dead + kv.Value.Live >= minCount).ToImmutableDictionary();

                await writer.WriteLineAsync(header).ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', header.Length)).ConfigureAwait(false);

                var maxSize = rawDict.Keys.Max(x => x.SizeBytes);

                var maxTotal = dict.Select(x => x.Value.Dead + x.Value.Live).Max();
                var maxDead = dict.Select(x => x.Value.Dead).Max();
                var maxLive = dict.Select(x => x.Value.Live).Max();

                var maxTotalSize = maxTotal * maxSize;
                var maxDeadSize = maxDead * maxSize;
                var maxLiveSize = maxLive * maxSize;

                var totalSize = maxTotal.ToString("N0").Length + $"({maxTotalSize:N0})".Length;
                totalSize = Math.Max("Total(bytes)".Length, totalSize);

                var deadSize = maxDead.ToString("N0").Length + $"({maxDeadSize:N0})".Length;
                deadSize = Math.Max("Dead(bytes)".Length, deadSize);

                var liveSize = maxLive.ToString("N0").Length + $"({maxLiveSize:N0})".Length;
                liveSize = Math.Max("Live(bytes)".Length, liveSize);

                await WritePartAsync(writer, "Total(bytes)", totalSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Dead(bytes)", deadSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Live(bytes)", liveSize).ConfigureAwait(false);
                await writer.WriteAsync("   Value").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                await writer.WriteLineAsync(new string('-', totalSize + deadSize + liveSize + "Value".Length + 3 * 3)).ConfigureAwait(false);

                var inOrder = dict.OrderByDescending(kv => (kv.Value.Dead + kv.Value.Live) * kv.Key.SizeBytes).ThenByDescending(kv => kv.Value.Dead * kv.Key.SizeBytes).ThenByDescending(kv => kv.Value.Live * kv.Key.SizeBytes);
                foreach (var kv in inOrder)
                {
                    var total = kv.Value.Live + kv.Value.Dead;
                    var totalBytes = total * kv.Key.SizeBytes;
                    var liveBytes = kv.Value.Live * kv.Key.SizeBytes;
                    var deadBytes = kv.Value.Dead * kv.Key.SizeBytes;

                    await WritePartAsync(writer, total.ToString("N0") + $"({totalBytes:N0})", totalSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Dead.ToString("N0") + $"({deadBytes:N0})", deadSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Live.ToString("N0") + $"({liveBytes:N0})", liveSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    var escaped = Escape(kv.Key.Name, builder);
                    await writer.WriteAsync(escaped).ConfigureAwait(false);
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }
            }

            static async ValueTask WriteSegmentAsync(
                TextWriter writer,
                string header,
                ImmutableDictionary<string, ReferenceStats> rawDict, 
                int minCount,
                StringBuilder builder
            )
            {
                var dict = rawDict.Where(kv => kv.Value.Dead + kv.Value.Live >= minCount).ToImmutableDictionary();

                await writer.WriteLineAsync(header).ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', header.Length)).ConfigureAwait(false);

                var maxTotal = dict.Select(x => x.Value.Dead + x.Value.Live).Max();
                var maxDead = dict.Select(x => x.Value.Dead).Max();
                var maxLive = dict.Select(x => x.Value.Live).Max();

                var totalSize = maxTotal.ToString("N0").Length;
                totalSize = Math.Max("Total".Length, totalSize);

                var deadSize = maxDead.ToString("N0").Length;
                deadSize = Math.Max("Dead".Length, deadSize);

                var liveSize = maxLive.ToString("N0").Length;
                liveSize = Math.Max("Live".Length, liveSize);

                await WritePartAsync(writer, "Total", totalSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Dead", deadSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Live", liveSize).ConfigureAwait(false);
                await writer.WriteAsync("   Value").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                await writer.WriteLineAsync(new string('-', totalSize + deadSize + liveSize + "Value".Length + 3 * 3)).ConfigureAwait(false);

                foreach (var kv in dict.OrderByDescending(kv => kv.Value.Dead + kv.Value.Live).ThenByDescending(kv => kv.Value.Dead).ThenByDescending(kv => kv.Value.Live))
                {
                    var total = kv.Value.Live + kv.Value.Dead;
                    await WritePartAsync(writer, total.ToString("N0"), totalSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Dead.ToString("N0"), deadSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Live.ToString("N0"), liveSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    var escaped = Escape(kv.Key, builder);
                    await writer.WriteAsync(escaped).ConfigureAwait(false);
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }
            }

            static async ValueTask WritePartAsync(TextWriter writer, string value, int size)
            {
                Debug.Assert(value.Length <= size);

                // right align the values
                if (value.Length != size)
                {
                    await writer.WriteAsync(new string(' ', size - value.Length)).ConfigureAwait(false);
                }

                await writer.WriteAsync(value).ConfigureAwait(false);
            }

            static string Escape(string value, StringBuilder builder)
            {
                if (!value.Any(x => NeedsEscape(x)))
                {
                    return value;
                }

                builder.Clear();
                var literal = builder;
                literal.Append("ESCAPED: ");
                literal.Append("\"");
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '\"': literal.Append("\\\""); break;
                        case '\\': literal.Append(@"\\"); break;
                        case '\0': literal.Append(@"\0"); break;
                        case '\a': literal.Append(@"\a"); break;
                        case '\b': literal.Append(@"\b"); break;
                        case '\f': literal.Append(@"\f"); break;
                        case '\n': literal.Append(@"\n"); break;
                        case '\r': literal.Append(@"\r"); break;
                        case '\t': literal.Append(@"\t"); break;
                        case '\v': literal.Append(@"\v"); break;
                        default:
                            // ASCII printable character
                            if (c >= 0x20 && c <= 0x7e)
                            {
                                literal.Append(c);
                                // As UTF16 escaped character
                            }
                            else
                            {
                                literal.Append(@"\u");
                                literal.Append(((int)c).ToString("x4"));
                            }
                            break;
                    }
                }
                literal.Append("\"");
                return literal.ToString();

                static bool NeedsEscape(char c)
                {
                    switch (c)
                    {
                        case '\0':
                        case '\a':
                        case '\b':
                        case '\f':
                        case '\n':
                        case '\r':
                        case '\t':
                        case '\v': return true;
                        default:
                            // ASCII printable character
                            if (c >= 0x20 && c <= 0x7e)
                            {
                                return false;
                            }
                            else
                            {
                                return true;
                            }
                    }
                }
            }
        }
    }
}
