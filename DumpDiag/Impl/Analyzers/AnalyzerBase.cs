using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThreadState = System.Threading.ThreadState;

namespace DumpDiag.Impl
{
    internal abstract class AnalyzerBase : IAsyncDisposable
    {
        private sealed class Message : IDisposable
        {
            private static readonly ThreadAffinitizedObjectPool<Message> ObjectPool = new ThreadAffinitizedObjectPool<Message>();

            internal Command FirstCommand { get; private set; }
            internal Command? SecondCommand { get; private set; }

            private BoundedSharedChannel<OwnedSequence<char>>? _response;
            internal BoundedSharedChannel<OwnedSequence<char>> Response
            {
                get
                {
                    if (_response == null)
                    {
                        throw new InvalidOperationException("Attempted to use uninitialized Message");
                    }

                    return _response;
                }
            }

            [Obsolete("Do not use this directly, it's only provided for internal pooling purposes")]
            public Message() { }

            private void Initialize(Command firstCommand, Command? secondCommand, BoundedSharedChannel<OwnedSequence<char>> response)
            {
                FirstCommand = firstCommand;
                SecondCommand = secondCommand;
                _response = response;
            }

            internal static Message Create(Command firstCommand, Command? secondCommand, BoundedSharedChannel<OwnedSequence<char>> response)
            {
                var ret = ObjectPool.Obtain();
                ret.Initialize(firstCommand, secondCommand, response);

                return ret;
            }

            public void Dispose()
            {
                FirstCommand = default;
                SecondCommand = null;
                _response = null;

                ObjectPool.Return(this);
            }
        }

        private readonly Thread thread;
        private readonly SemaphoreSlim threadReadySignal;

        private readonly SemaphoreSlim messageReadySignal;
        private Message? message;

        private bool disposed;

        private readonly Encoding underlyingInputEncoding;
        private readonly Stream underlyingInput;

        private readonly Encoding underlyingOutputEncoding;
        private readonly Stream underlyingOutput;

        private readonly string newLine;

        protected ArrayPool<char> ArrayPool { get; }

        protected AnalyzerBase(Stream inputStream, Encoding inputEncoding, Stream outputStream, Encoding outputEncoding, string newLine, ArrayPool<char> arrayPool)
        {
            this.newLine = newLine;

            ArrayPool = arrayPool;

            thread = new Thread(ThreadLoop);
            thread.Name = $"{GetType().FullName} Thread";
            threadReadySignal = new SemaphoreSlim(0);
            messageReadySignal = new SemaphoreSlim(0);

            underlyingInputEncoding = inputEncoding;
            underlyingInput = inputStream;

            underlyingOutputEncoding = outputEncoding;
#if DEBUG
            underlyingOutput = new EchoTextStream(outputStream, underlyingOutputEncoding);
#else
            underlyingOutput = outputStream;
#endif

            disposed = false;
        }

        protected virtual ValueTask StartAsync()
        {
            if (thread.ThreadState != ThreadState.Unstarted)
            {
                throw new InvalidOperationException("Double start");
            }

            thread.Start();

            return new ValueTask(threadReadySignal.WaitAsync());
        }

        private void ThreadLoop()
        {
            const string EndCommandOutput = "<END_COMMAND_OUTPUT>";
            const string PromptStart = "> ";

            using var reader = new ProcessStreamReader(ArrayPool, underlyingOutput, underlyingOutputEncoding, newLine);

            var endCommandSpan = EndCommandOutput.AsSpan();
            var promptStartSpan = PromptStart.AsSpan();

            var e = reader.ReadAllLines().GetEnumerator();
            try
            {
                while (e.MoveNext())
                {
                    var line = e.Current;
                    var asSequence = line.GetSequence();
                    if (asSequence.Equals(endCommandSpan, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        break;
                    }

                    line.Dispose();
                }

                threadReadySignal.Release();

                // start processesing requests
                while (true)
                {
                    messageReadySignal.Wait();
                    using var message = Interlocked.Exchange(ref this.message, null);

                    if (message == null)
                    {
                        break;
                    }

                    message.FirstCommand.Write(underlyingInput, newLine, underlyingInputEncoding);
                    PushCommandResults(ref e, message.Response, endCommandSpan, promptStartSpan);

                    if (message.SecondCommand != null)
                    {
                        message.SecondCommand.Value.Write(underlyingInput, newLine, underlyingInputEncoding);
                        PushCommandResults(ref e, message.Response, endCommandSpan, promptStartSpan);
                    }

                    message.Response.Complete();
                }
            }
            finally
            {
                e.Dispose();
            }

            static void PushCommandResults(ref ProcessStreamReader.Enumerator e, BoundedSharedChannel<OwnedSequence<char>> writer, ReadOnlySpan<char> endCommand, ReadOnlySpan<char> promptStart)
            {
                var readPrompt = false;

                while (e.MoveNext())
                {
                    var line = e.Current;

                    var asSequence = line.GetSequence();

                    // skip the prompt line...
                    if (!readPrompt && asSequence.StartsWith(promptStart, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        readPrompt = true;
                        continue;
                    }

                    // stop once we see the magic end command string
                    if (asSequence.Equals(endCommand, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        break;
                    }

                    writer.Append(line);
                }
            }
        }

        protected ValueTask DisposeInnerAsync()
        {
            Debug.Assert(!disposed);

            if (thread.ThreadState == ThreadState.Unstarted)
            {
                // never started or already disposed
                disposed = true;
                return default;
            }

            disposed = true;

            Interlocked.Exchange(ref message, null);
            messageReadySignal.Release();

            thread.Join();

            threadReadySignal.Dispose();
            messageReadySignal.Dispose();

            return default;
        }

        public abstract ValueTask DisposeAsync();

        // internal for testing purposes
        internal BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable SendCommand(Command command, Command? secondCommand = null)
        {
            Debug.Assert(!disposed);

            var response = BoundedSharedChannel<OwnedSequence<char>>.Create();

            var msg = Message.Create(command, secondCommand, response);

            var oldMsg = Interlocked.Exchange(ref message, msg);
            Debug.Assert(oldMsg == null, $"Old message was: {oldMsg}, new message is {msg}");
            messageReadySignal.Release();

            return response.ReadUntilCompletedAsync();
        }
    }
}
