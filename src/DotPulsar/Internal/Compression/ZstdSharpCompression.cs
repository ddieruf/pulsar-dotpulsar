﻿/*
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace DotPulsar.Internal.Compression;

using DotPulsar.Exceptions;
using DotPulsar.Internal.Abstractions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class ZstdSharpCompression
{
    public delegate Span<byte> Wrap(ReadOnlySpan<byte> src);
    public delegate int Unwrap(byte[] src, byte[] dst, int offset);

    public static bool TryLoading(out ICompressorFactory? compressorFactory, out IDecompressorFactory? decompressorFactory)
    {
        try
        {
            var assembly = Assembly.Load("ZstdSharp");

            var definedTypes = assembly.DefinedTypes.ToArray();

            var decompressorType = FindDecompressor(definedTypes, "ZstdSharp.Decompressor");
            var decompressorMethods = decompressorType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var unwrapMethod = FindUnwrap(decompressorMethods);

            var compressorType = FindCompressor(definedTypes, "ZstdSharp.Compressor");
            var compressorMethods = compressorType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var wrapMethod = FindWrap(compressorMethods);

            compressorFactory = new CompressorFactory(PulsarApi.CompressionType.Zstd, () =>
            {
                var compressor = Activator.CreateInstance(compressorType, 0);
                if (compressor is null)
                    throw new Exception($"Activator.CreateInstance returned null when trying to create a {compressorType.FullName}");

                var wrap = (Wrap) wrapMethod.CreateDelegate(typeof(Wrap), compressor);
                return new Compressor(CreateCompressor(wrap), (IDisposable) compressor);
            });

            decompressorFactory = new DecompressorFactory(PulsarApi.CompressionType.Zstd, () =>
            {
                var decompressor = Activator.CreateInstance(decompressorType);
                if (decompressor is null)
                    throw new Exception($"Activator.CreateInstance returned null when trying to create a {decompressorType.FullName}");

                var unwrap = (Unwrap) unwrapMethod.CreateDelegate(typeof(Unwrap), decompressor);
                return new Decompressor(CreateDecompressor(unwrap), (IDisposable) decompressor);
            });

            return CompressionTester.TestCompression(compressorFactory, decompressorFactory);
        }
        catch
        {
            // Ignore
        }

        compressorFactory = null;
        decompressorFactory = null;

        return false;
    }

    private static TypeInfo FindDecompressor(IEnumerable<TypeInfo> types, string fullName)
    {
        foreach (var type in types)
        {
            if (type.FullName is null || !type.FullName.Equals(fullName))
                continue;

            if (type.IsPublic &&
                type.IsClass &&
                !type.IsAbstract &&
                type.ImplementedInterfaces.Contains(typeof(IDisposable)) &&
                type.GetConstructor(Type.EmptyTypes) is not null)
                return type;

            break;
        }

        throw new Exception($"{fullName} as a public class with an empty public constructor and implementing IDisposable was not found");
    }

    private static TypeInfo FindCompressor(IEnumerable<TypeInfo> types, string fullName)
    {
        foreach (var type in types)
        {
            if (type.FullName is null || !type.FullName.Equals(fullName))
                continue;

            if (type.IsPublic &&
                type.IsClass &&
                !type.IsAbstract &&
                type.ImplementedInterfaces.Contains(typeof(IDisposable)) &&
                type.GetConstructor(new[] { typeof(int) }) is not null)
                return type;

            break;
        }

        throw new Exception($"{fullName} as a public class with an public constructor taking an integer and implementing IDisposable was not found");
    }

    private static MethodInfo FindWrap(MethodInfo[] methods)
    {
        const string name = "Wrap";

        foreach (var method in methods)
        {
            if (method.Name != name || method.ReturnType != typeof(Span<byte>))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;

            if (parameters[0].ParameterType != typeof(ReadOnlySpan<byte>))
                continue;

            return method;
        }

        throw new Exception($"A method with the name '{name}' matching the delegate was not found");
    }

    private static MethodInfo FindUnwrap(MethodInfo[] methods)
    {
        const string name = "Unwrap";

        foreach (var method in methods)
        {
            if (method.Name != name || method.ReturnType != typeof(int))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 3)
                continue;

            if (parameters[0].ParameterType != typeof(byte[]) ||
                parameters[1].ParameterType != typeof(byte[]) ||
                parameters[2].ParameterType != typeof(int))
                continue;

            return method;
        }

        throw new Exception($"A method with the name '{name}' matching the delegate was not found");
    }

    private static Func<ReadOnlySequence<byte>, int, ReadOnlySequence<byte>> CreateDecompressor(Unwrap decompress)
    {
        return (source, size) =>
        {
            var decompressed = new byte[size];
            var bytesDecompressed = decompress(source.ToArray(), decompressed, 0);
            if (size == bytesDecompressed)
                return new ReadOnlySequence<byte>(decompressed);

            throw new CompressionException($"ZstdSharp.Decompressor returned {bytesDecompressed} but expected {size}");
        };
    }

    private static Func<ReadOnlySequence<byte>, ReadOnlySequence<byte>> CreateCompressor(Wrap compress)
        => (source) => new ReadOnlySequence<byte>(compress(source.ToArray().AsSpan()).ToArray());
}
