#if NET6_0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetPilot
{
    internal static class LegacyBclExtensions
    {
        internal static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null)
            => source.OrderBy(value => value, comparer);
        internal static IOrderedEnumerable<T> OrderDescending<T>(this IEnumerable<T> source, IComparer<T>? comparer = null)
            => source.OrderByDescending(value => value, comparer);

        internal static async ValueTask<int> ReadAtLeastAsync(this Stream stream, Memory<byte> buffer, int minimumBytes,
            bool throwOnEndOfStream = true, CancellationToken cancellationToken = default)
        {
            if (minimumBytes < 0 || minimumBytes > buffer.Length) throw new ArgumentOutOfRangeException(nameof(minimumBytes));
            int total = 0;
            while (total < minimumBytes)
            {
                int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (throwOnEndOfStream) throw new EndOfStreamException("Поток завершился до получения полного сообщения.");
                    break;
                }
                total += read;
            }
            return total;
        }

        internal static async ValueTask ReadExactlyAsync(this Stream stream, Memory<byte> buffer, CancellationToken token = default)
        {
            while (!buffer.IsEmpty)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Поток завершился до получения полного сообщения.");
                buffer = buffer[read..];
            }
        }

        internal static void ReadExactly(this Stream stream, Span<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int read = stream.Read(buffer);
                if (read == 0) throw new EndOfStreamException("Поток завершился до получения полного сообщения.");
                buffer = buffer[read..];
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
        public bool IsOptional { get; init; }
    }
}
#endif
