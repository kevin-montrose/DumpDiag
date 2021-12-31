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
        internal ImmutableDictionary<TypeDetails, ReferenceStats> TypeReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> StringReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> DelegateReferenceStats { get; }
        internal ImmutableDictionary<string, ReferenceStats> CharacterArrayReferenceStats { get; }
        internal ImmutableDictionary<TypeDetails, ReferenceStats> AsyncStateMatchineStats { get; }
        internal ThreadAnalysis ThreadDetails { get; }
        internal ImmutableList<AsyncMachineBreakdown> AsyncStateMachineBreakdown { get; }
        internal PinAnalysis PinAnalysis { get; }
        internal HeapFragmentation HeapFragmentation { get; }

        internal AnalyzeResult(
            ImmutableDictionary<TypeDetails, ReferenceStats> typeReferenceStats,
            ImmutableDictionary<string, ReferenceStats> stringReferenceStats,
            ImmutableDictionary<string, ReferenceStats> delegateReferenceStats,
            ImmutableDictionary<string, ReferenceStats> characterArrayReferenceStats,
            ImmutableDictionary<TypeDetails, ReferenceStats> asyncStateMatchineStats,
            ThreadAnalysis threadDetails,
            ImmutableList<AsyncMachineBreakdown> asyncStateMachineBreakdown,
            PinAnalysis pinAnalysis,
            HeapFragmentation heapFragmentation
        )
        {
            TypeReferenceStats = typeReferenceStats;
            StringReferenceStats = stringReferenceStats;
            DelegateReferenceStats = delegateReferenceStats;
            CharacterArrayReferenceStats = characterArrayReferenceStats;
            AsyncStateMatchineStats = asyncStateMatchineStats;
            ThreadDetails = threadDetails;
            AsyncStateMachineBreakdown = asyncStateMachineBreakdown;
            PinAnalysis = pinAnalysis;
            HeapFragmentation = heapFragmentation;
        }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                var task = WriteToAsync(writer, 1, 0);

                task.GetAwaiter().GetResult();

                return writer.ToString();
            }
        }

        internal async ValueTask WriteToAsync(TextWriter writer, int minCount, int asyncMachineSizeCutoff)
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

            await WriteAsyncMachineDetailsAsync(writer, AsyncStateMachineBreakdown, asyncMachineSizeCutoff).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteUniqueStackFramesAsync(writer, ThreadDetails).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteCallStacksAsync(writer, ThreadDetails).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WriteHeapFragmentationAsync(writer, HeapFragmentation).ConfigureAwait(false);

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            await WritePinAnalysisAsync(writer, PinAnalysis).ConfigureAwait(false);

            static async ValueTask WriteHeapFragmentationAsync(TextWriter writer, HeapFragmentation frag)
            {
                var rowsBuilder = ImmutableList.CreateBuilder<(string Heap, long SizeBytes, long FreeBytes, double FragmentationPercent)>();

                rowsBuilder.AddRange(
                    new[]
                    {
                        (Heap: "Gen0", SizeBytes: frag.Gen0Size, FreeBytes: frag.Gen0Free, FragmentationPercent: frag.Gen0FragementationPercent),
                        (Heap: "Gen1", SizeBytes: frag.Gen1Size, FreeBytes: frag.Gen1Free, FragmentationPercent: frag.Gen1FragementationPercent),
                        (Heap: "Gen2", SizeBytes: frag.Gen2Size, FreeBytes: frag.Gen2Free, FragmentationPercent: frag.Gen2FragementationPercent),
                        (Heap: "LargeObject", SizeBytes: frag.LOHSize, FreeBytes: frag.LOHFree, FragmentationPercent: frag.LOHFragementationPercent)
                    }
                );

                if (frag.POHSize > 0)
                {
                    rowsBuilder.Add(
                        (Heap: "PinnedObject", SizeBytes: frag.POHSize, FreeBytes: frag.POHFree, FragmentationPercent: frag.POHFragementationPercent)
                    );
                }

                var rows = rowsBuilder.ToImmutable();

                var maxGenSize = rows.Max(m => m.Heap.Length);
                var maxFreeSize = rows.Max(m => m.FreeBytes).ToString("N0").Length;
                var maxSizeSize = rows.Max(m => m.SizeBytes).ToString("N0").Length;
                var maxFragSize = rows.Max(m => $"{m.FragmentationPercent:N1}%".Length);

                var genSize = Math.Max(maxGenSize, "Type".Length);
                var freeSize = Math.Max(maxFreeSize, "Free Bytes".Length);
                var sizeSize = Math.Max(maxSizeSize, "Size Bytes".Length);
                var fragSize = Math.Max(maxFragSize, "Fragmentation %".Length);

                await writer.WriteLineAsync("Heap Fragmentation").ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', "Heap Fragmentation".Length)).ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                await WritePartAsync(writer, "Type", genSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Size Bytes", sizeSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Free Bytes", freeSize).ConfigureAwait(false);
                await writer.WriteAsync("   ").ConfigureAwait(false);
                await WritePartAsync(writer, "Fragmentation %", fragSize).ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteLineAsync(new string('-', genSize + sizeSize + freeSize + fragSize + 3 * 3)).ConfigureAwait(false);

                var inOrder =
                    rows
                        .OrderByDescending(x => x.FragmentationPercent)
                        .ThenBy(x => x.Heap)
                        .ThenByDescending(x => x.SizeBytes)
                        .ThenByDescending(x => x.FreeBytes);

                foreach (var row in inOrder)
                {
                    await WritePartAsync(writer, row.Heap, genSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    await WritePartAsync(writer, $"{row.SizeBytes:N0}", sizeSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    await WritePartAsync(writer, $"{row.FreeBytes:N0}", freeSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    await WritePartAsync(writer, $"{row.FragmentationPercent:N1}%", sizeSize).ConfigureAwait(false);
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }
            }

            static async ValueTask WritePinAnalysisAsync(TextWriter writer, PinAnalysis pins)
            {
                if (pins.AsyncPins.Count == 0 && pins.Pins.Count == 0)
                {
                    return;
                }

                var wroteAsyncPins = await WriteSectionAsync(writer, "Async Pins", pins.AsyncPins, false).ConfigureAwait(false);

                await WriteSectionAsync(writer, "Explicit Pins", pins.Pins, wroteAsyncPins).ConfigureAwait(false);

                static async ValueTask<bool> WriteSectionAsync(
                    TextWriter writer,
                    string sectionName,
                    ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> section,
                    bool needsSpacer
                )
                {
                    if (section.Count == 0)
                    {
                        return false;
                    }

                    if (needsSpacer)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await writer.WriteLineAsync(sectionName).ConfigureAwait(false);
                    await writer.WriteLineAsync(new string('=', sectionName.Length)).ConfigureAwait(false);

                    var maxTypeName = section.Max(kv => kv.Value.Max(x => x.Key.TypeName.Length));
                    var maxClassification = section.Max(kv => kv.Key.ToString().Length);
                    var maxCountPlusBytes = section.Max(kv => kv.Value.Max(v => $"{v.Value.Count:N0}({v.Value.Size:N0})".Length));

                    var typeNameSize = Math.Max(maxTypeName, "Type".Length);
                    var heapSize = Math.Max(maxClassification, "Location".Length);
                    var countPlusBytesSize = Math.Max(maxClassification, "Count(bytes)".Length);

                    await WritePartAsync(writer, "Type", typeNameSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, "Location", heapSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, "Count(bytes)", countPlusBytesSize).ConfigureAwait(false);
                    await writer.WriteLineAsync().ConfigureAwait(false);
                    await writer.WriteLineAsync(new string('-', typeNameSize + heapSize + countPlusBytesSize + 2 * 3)).ConfigureAwait(false);

                    var rows =
                        section
                            .SelectMany(
                                kv =>
                                    kv.Value.Select(
                                        x =>
                                            (
                                                Type: x.Key,
                                                Heap: kv.Key,
                                                Count: x.Value.Count,
                                                Size: x.Value.Size
                                            )
                                    )
                            )
                            .OrderByDescending(x => x.Size)
                            .ThenByDescending(x => x.Count)
                            .ThenBy(x => x.Type)
                            .ThenBy(x => x.Heap);

                    foreach (var row in rows)
                    {
                        await WritePartAsync(writer, row.Type.TypeName, typeNameSize).ConfigureAwait(false);
                        await writer.WriteAsync("   ").ConfigureAwait(false);

                        await WritePartAsync(writer, row.Heap.ToString(), heapSize).ConfigureAwait(false);
                        await writer.WriteAsync("   ").ConfigureAwait(false);

                        await WritePartAsync(writer, $"{row.Count:N0}({row.Size:N0})", countPlusBytesSize).ConfigureAwait(false);

                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    return true;
                }
            }

            static async ValueTask WriteAsyncMachineDetailsAsync(TextWriter writer, ImmutableList<AsyncMachineBreakdown> breakdowns, int cutoffBytes)
            {
                var afterCutoff = breakdowns.Where(x => x.StateSizeBytes >= cutoffBytes).ToImmutableList();
                if (afterCutoff.IsEmpty)
                {
                    return;
                }

                await writer.WriteLineAsync("Large Async State Machines").ConfigureAwait(false);
                await writer.WriteLineAsync(new string('=', "Large Async State Machines".Length)).ConfigureAwait(false);

                var inOrder =
                    afterCutoff
                        .Where(x => x.StateSizeBytes >= cutoffBytes)
                        .OrderByDescending(x => x.StateSizeBytes)
                        .ThenByDescending(x => x.StateMachineFields.Count);

                var maxFieldName = afterCutoff.Max(x => x.StateMachineFields.IsEmpty ? 0 : x.StateMachineFields.Max(y => y.InstanceField.Name.Length));

                foreach (var machine in inOrder)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);

                    var nameWithSize = $"{machine.Type.TypeName} ({machine.StateSizeBytes:N0} bytes) ({machine.StateMachineFields.Count:N0} fields in state)";
                    await writer.WriteLineAsync(nameWithSize).ConfigureAwait(false);

                    if (machine.StateMachineFields.IsEmpty)
                    {
                        // todo: this can probably be improved...
                        // as it is we're looking at the instances on the heap, but we could probably inspect more
                        // type information to figure out what fields are expected without an instance
                        await writer.WriteLineAsync(new string('-', nameWithSize.Length)).ConfigureAwait(false);
                        await writer.WriteLineAsync("No valid examples to inspect found on heap, reporting only size");
                    }
                    else
                    {
                        await WritePartAsync(writer, "Field", maxFieldName).ConfigureAwait(false);
                        await writer.WriteLineAsync("   Type").ConfigureAwait(false);
                        await writer.WriteLineAsync(new string('-', maxFieldName + 3 + "Type".Length)).ConfigureAwait(false);

                        foreach (var field in machine.StateMachineFields.OrderBy(x => x.InstanceField.Name).ThenBy(x => x.TypeDetails.TypeName))
                        {
                            await WritePartAsync(writer, field.InstanceField.Name, maxFieldName).ConfigureAwait(false);
                            await writer.WriteAsync("   ").ConfigureAwait(false);
                            await writer.WriteLineAsync(field.TypeDetails.TypeName);
                        }
                    }
                }
            }

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
                ImmutableDictionary<TypeDetails, ReferenceStats> rawDict,
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

                var maxTotalSize = rawDict.Max(x => x.Value.LiveBytes + x.Value.DeadBytes);
                var maxDeadSize = rawDict.Max(x => x.Value.DeadBytes);
                var maxLiveSize = rawDict.Max(x => x.Value.LiveBytes);

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

                var inOrder =
                    dict
                        .OrderByDescending(kv => kv.Value.DeadBytes + kv.Value.LiveBytes)
                        .ThenByDescending(kv => kv.Value.DeadBytes)
                        .ThenByDescending(kv => kv.Value.LiveBytes);
                foreach (var kv in inOrder)
                {
                    var total = kv.Value.Live + kv.Value.Dead;
                    var totalBytes = kv.Value.LiveBytes + kv.Value.DeadBytes;
                    var liveBytes = kv.Value.LiveBytes;
                    var deadBytes = kv.Value.DeadBytes;

                    await WritePartAsync(writer, total.ToString("N0") + $"({totalBytes:N0})", totalSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Dead.ToString("N0") + $"({deadBytes:N0})", deadSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);
                    await WritePartAsync(writer, kv.Value.Live.ToString("N0") + $"({liveBytes:N0})", liveSize).ConfigureAwait(false);
                    await writer.WriteAsync("   ").ConfigureAwait(false);

                    var escaped = Escape(kv.Key.TypeName, builder);
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
                Debug.Assert(value.Length <= size, $"{value} ; {size}");

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
