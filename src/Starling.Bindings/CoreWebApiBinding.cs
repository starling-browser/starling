using System.Globalization;
using System.Text;
using Starling.Js.Intrinsics;
using Starling.Js.Runtime;

namespace Starling.Bindings;

public static class CoreWebApiBinding
{
    public static void Install(JsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var realm = runtime.Realm;
        if (realm.GlobalObject.GetOwnPropertyDescriptor("URL") is not null)
        {
            return;
        }

        InstallUrlSearchParams(realm);
        InstallUrl(realm);
        InstallBlobAndFile(realm);
        InstallFormData(realm);
        InstallTextEncoding(realm);
        InstallBase64(realm);
        InstallStructuredClone(realm);
    }

    private static void InstallUrlSearchParams(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineMethod(realm, proto, "append", (thisV, args) =>
        {
            var p = UrlSearchParamsObject.Require(realm, thisV);
            p.Append(ArgString(args, 0), ArgString(args, 1));
            return JsValue.Undefined;
        }, 2);
        EventTargetBinding.DefineMethod(realm, proto, "delete", (thisV, args) =>
        {
            var p = UrlSearchParamsObject.Require(realm, thisV);
            p.DeleteEntry(ArgString(args, 0));
            return JsValue.Undefined;
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "get", (thisV, args) =>
        {
            var v = UrlSearchParamsObject.Require(realm, thisV).GetEntry(ArgString(args, 0));
            return v is null ? JsValue.Null : JsValue.String(v);
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "getAll", (thisV, args) =>
        {
            var arr = new JsArray(realm);
            foreach (var v in UrlSearchParamsObject.Require(realm, thisV).GetAll(ArgString(args, 0)))
            {
                arr.Push(JsValue.String(v));
            }

            return JsValue.Object(arr);
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "has", (thisV, args) =>
            JsValue.Boolean(UrlSearchParamsObject.Require(realm, thisV).HasEntry(ArgString(args, 0))), 1);
        EventTargetBinding.DefineMethod(realm, proto, "set", (thisV, args) =>
        {
            var p = UrlSearchParamsObject.Require(realm, thisV);
            p.Set(ArgString(args, 0), ArgString(args, 1));
            return JsValue.Undefined;
        }, 2);
        EventTargetBinding.DefineMethod(realm, proto, "sort", (thisV, _) =>
        {
            UrlSearchParamsObject.Require(realm, thisV).Sort();
            return JsValue.Undefined;
        }, 0);
        EventTargetBinding.DefineMethod(realm, proto, "toString", (thisV, _) =>
            JsValue.String(UrlSearchParamsObject.Require(realm, thisV).Serialize()), 0);
        EventTargetBinding.DefineMethod(realm, proto, "forEach", (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("URLSearchParams.forEach requires a callable"));
            }

            var p = UrlSearchParamsObject.Require(realm, thisV);
            var cb = args[0];
            var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
            foreach (var (name, value) in p.Entries)
            {
                AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { JsValue.String(value), JsValue.String(name), thisV });
            }

            return JsValue.Undefined;
        }, 1);
        InstallPairIterators(realm, proto, thisV => UrlSearchParamsObject.Require(realm, thisV).Entries);

        var ctor = new JsNativeFunction(realm, "URLSearchParams", 0, (_, args) =>
        {
            var p = new UrlSearchParamsObject(proto);
            p.Populate(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
            return JsValue.Object(p);
        }, isConstructor: true);
        DefineCtor(realm, ctor, proto, "URLSearchParams");
    }

    private static void InstallUrl(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineAccessor(realm, proto, "href",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Href),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetHref(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "origin",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Origin));
        EventTargetBinding.DefineAccessor(realm, proto, "protocol",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Protocol),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetProtocol(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "host",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Host),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetHost(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "hostname",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Hostname),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetHostname(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "port",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Port),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetPort(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "pathname",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Pathname),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetPathname(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "search",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Search),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetSearch(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "hash",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Hash),
            (thisV, args) =>
            {
                UrlObject.Require(realm, thisV).SetHash(ArgString(args, 0));
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, proto, "searchParams",
            (thisV, _) => JsValue.Object(UrlObject.Require(realm, thisV).SearchParams));
        EventTargetBinding.DefineMethod(realm, proto, "toString",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Href), 0);
        EventTargetBinding.DefineMethod(realm, proto, "toJSON",
            (thisV, _) => JsValue.String(UrlObject.Require(realm, thisV).Href), 0);

        var ctor = new JsNativeFunction(realm, "URL", 1, (_, args) =>
        {
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("URL requires an input"));
            }

            var input = JsValue.ToStringValue(args[0]);
            var baseUrl = args.Length > 1 && !args[1].IsUndefined ? JsValue.ToStringValue(args[1]) : null;
            return JsValue.Object(new UrlObject(proto, realm, input, baseUrl));
        }, isConstructor: true);
        DefineCtor(realm, ctor, proto, "URL");
    }

    private static void InstallBlobAndFile(JsRealm realm)
    {
        var blobProto = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineAccessor(realm, blobProto, "size",
            (thisV, _) => JsValue.Number(BlobObject.Require(realm, thisV).Bytes.Length));
        EventTargetBinding.DefineAccessor(realm, blobProto, "type",
            (thisV, _) => JsValue.String(BlobObject.Require(realm, thisV).Type));
        EventTargetBinding.DefineMethod(realm, blobProto, "text", (thisV, _) =>
            FetchBinding.ResolvedPromise(realm, JsValue.String(Encoding.UTF8.GetString(BlobObject.Require(realm, thisV).Bytes))), 0);
        EventTargetBinding.DefineMethod(realm, blobProto, "arrayBuffer", (thisV, _) =>
            FetchBinding.ResolvedPromise(realm, JsValue.Object(NewArrayBuffer(realm, BlobObject.Require(realm, thisV).Bytes))), 0);
        EventTargetBinding.DefineMethod(realm, blobProto, "slice", (thisV, args) =>
        {
            var b = BlobObject.Require(realm, thisV);
            var start = ClampSlice(args.Length > 0 ? args[0] : JsValue.Undefined, b.Bytes.Length, 0);
            var end = ClampSlice(args.Length > 1 ? args[1] : JsValue.Undefined, b.Bytes.Length, b.Bytes.Length);
            var count = Math.Max(end - start, 0);
            var bytes = new byte[count];
            Array.Copy(b.Bytes, start, bytes, 0, count);
            var type = args.Length > 2 && !args[2].IsUndefined ? NormalizeMimeType(JsValue.ToStringValue(args[2])) : "";
            return JsValue.Object(new BlobObject(blobProto, bytes, type));
        }, 2);

        var blobCtor = new JsNativeFunction(realm, "Blob", 0, (_, args) =>
        {
            var bytes = BytesFromBlobParts(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
            var type = "";
            if (args.Length > 1 && args[1].IsObject)
            {
                var t = args[1].AsObject.Get("type");
                if (!t.IsUndefined)
                {
                    type = NormalizeMimeType(JsValue.ToStringValue(t));
                }
            }
            return JsValue.Object(new BlobObject(blobProto, bytes, type));
        }, isConstructor: true);
        DefineCtor(realm, blobCtor, blobProto, "Blob");

        var fileProto = new JsObject(blobProto);
        EventTargetBinding.DefineAccessor(realm, fileProto, "name",
            (thisV, _) => JsValue.String(FileObject.RequireFile(realm, thisV).Name));
        EventTargetBinding.DefineAccessor(realm, fileProto, "lastModified",
            (thisV, _) => JsValue.Number(FileObject.RequireFile(realm, thisV).LastModified));
        var fileCtor = new JsNativeFunction(realm, "File", 2, (_, args) =>
        {
            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("File requires fileBits and fileName"));
            }

            var bytes = BytesFromBlobParts(realm, args[0]);
            var name = JsValue.ToStringValue(args[1]);
            var type = "";
            var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (args.Length > 2 && args[2].IsObject)
            {
                var obj = args[2].AsObject;
                var t = obj.Get("type");
                if (!t.IsUndefined)
                {
                    type = NormalizeMimeType(JsValue.ToStringValue(t));
                }

                var lm = obj.Get("lastModified");
                if (lm.IsNumber)
                {
                    lastModified = (long)lm.AsNumber;
                }
            }
            return JsValue.Object(new FileObject(fileProto, bytes, type, name, lastModified));
        }, isConstructor: true);
        DefineCtor(realm, fileCtor, fileProto, "File");
    }

    private static void InstallFormData(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineMethod(realm, proto, "append", (thisV, args) =>
        {
            FormDataObject.Require(realm, thisV).Append(realm, ArgString(args, 0), args.Length > 1 ? args[1] : JsValue.Undefined, args.Length > 2 ? args[2] : JsValue.Undefined);
            return JsValue.Undefined;
        }, 2);
        EventTargetBinding.DefineMethod(realm, proto, "set", (thisV, args) =>
        {
            var fd = FormDataObject.Require(realm, thisV);
            fd.DeleteEntry(ArgString(args, 0));
            fd.Append(realm, ArgString(args, 0), args.Length > 1 ? args[1] : JsValue.Undefined, args.Length > 2 ? args[2] : JsValue.Undefined);
            return JsValue.Undefined;
        }, 2);
        EventTargetBinding.DefineMethod(realm, proto, "delete", (thisV, args) =>
        {
            FormDataObject.Require(realm, thisV).DeleteEntry(ArgString(args, 0));
            return JsValue.Undefined;
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "get", (thisV, args) =>
        {
            var v = FormDataObject.Require(realm, thisV).GetEntry(ArgString(args, 0));
            return v ?? JsValue.Null;
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "getAll", (thisV, args) =>
        {
            var arr = new JsArray(realm);
            foreach (var v in FormDataObject.Require(realm, thisV).GetAll(ArgString(args, 0)))
            {
                arr.Push(v);
            }

            return JsValue.Object(arr);
        }, 1);
        EventTargetBinding.DefineMethod(realm, proto, "has", (thisV, args) =>
            JsValue.Boolean(FormDataObject.Require(realm, thisV).HasEntry(ArgString(args, 0))), 1);
        EventTargetBinding.DefineMethod(realm, proto, "forEach", (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("FormData.forEach requires a callable"));
            }

            var cb = args[0];
            var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
            foreach (var entry in FormDataObject.Require(realm, thisV).Entries)
            {
                AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { entry.Value, JsValue.String(entry.Name), thisV });
            }

            return JsValue.Undefined;
        }, 1);
        InstallFormDataIterators(realm, proto);

        var ctor = new JsNativeFunction(realm, "FormData", 0, (_, _) => JsValue.Object(new FormDataObject(proto)), isConstructor: true);
        DefineCtor(realm, ctor, proto, "FormData");
    }

    private static void InstallTextEncoding(JsRealm realm)
    {
        var encProto = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineAccessor(realm, encProto, "encoding", (_, _) => JsValue.String("utf-8"));
        EventTargetBinding.DefineMethod(realm, encProto, "encode", (_, args) =>
            JsValue.Object(NewUint8Array(realm, Encoding.UTF8.GetBytes(ArgString(args, 0)))), 1);
        EventTargetBinding.DefineMethod(realm, encProto, "encodeInto", (_, args) =>
        {
            var source = ArgString(args, 0);
            if (args.Length < 2 || !args[1].IsObject || args[1].AsObject is not JsTypedArray dest || dest.Kind != JsTypedArrayKind.Uint8)
            {
                throw new JsThrow(realm.NewTypeError("encodeInto requires a Uint8Array destination"));
            }

            var written = 0;
            var read = 0;
            foreach (var rune in source.EnumerateRunes())
            {
                var bytes = Encoding.UTF8.GetBytes(rune.ToString());
                if (written + bytes.Length > dest.ByteLength)
                {
                    break;
                }

                bytes.CopyTo(dest.Buffer.GetSpan(dest.ByteOffset + written, bytes.Length));
                written += bytes.Length;
                read += rune.Utf16SequenceLength;
            }
            var result = new JsObject(realm.ObjectPrototype);
            result.DefineOwnProperty("read", PropertyDescriptor.Data(JsValue.Number(read), true, true, true));
            result.DefineOwnProperty("written", PropertyDescriptor.Data(JsValue.Number(written), true, true, true));
            return JsValue.Object(result);
        }, 2);
        var encCtor = new JsNativeFunction(realm, "TextEncoder", 0, (_, _) => JsValue.Object(new JsObject(encProto)), isConstructor: true);
        DefineCtor(realm, encCtor, encProto, "TextEncoder");

        var decProto = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineAccessor(realm, decProto, "encoding", (thisV, _) => JsValue.String(TextDecoderObject.Require(realm, thisV).EncodingName));
        EventTargetBinding.DefineAccessor(realm, decProto, "fatal", (thisV, _) => JsValue.Boolean(TextDecoderObject.Require(realm, thisV).Fatal));
        EventTargetBinding.DefineAccessor(realm, decProto, "ignoreBOM", (thisV, _) => JsValue.Boolean(TextDecoderObject.Require(realm, thisV).IgnoreBom));
        EventTargetBinding.DefineMethod(realm, decProto, "decode", (thisV, args) =>
        {
            var dec = TextDecoderObject.Require(realm, thisV);
            var bytes = args.Length == 0 || args[0].IsUndefined ? Array.Empty<byte>() : BytesFromBufferSource(realm, args[0]);
            try
            {
                var text = dec.Encoding.GetString(bytes);
                return JsValue.String(!dec.IgnoreBom && text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text);
            }
            catch (DecoderFallbackException ex)
            {
                throw new JsThrow(realm.NewTypeError(ex.Message));
            }
        }, 1);
        var decCtor = new JsNativeFunction(realm, "TextDecoder", 0, (_, args) =>
        {
            var label = args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : "utf-8";
            var fatal = false;
            var ignoreBom = false;
            if (args.Length > 1 && args[1].IsObject)
            {
                fatal = JsValue.ToBoolean(args[1].AsObject.Get("fatal"));
                ignoreBom = JsValue.ToBoolean(args[1].AsObject.Get("ignoreBOM"));
            }
            return JsValue.Object(new TextDecoderObject(realm, decProto, label, fatal, ignoreBom));
        }, isConstructor: true);
        DefineCtor(realm, decCtor, decProto, "TextDecoder");
    }

    private static void InstallBase64(JsRealm realm)
    {
        EventTargetBinding.DefineMethod(realm, realm.GlobalObject, "btoa", (_, args) =>
        {
            var s = ArgString(args, 0);
            var bytes = new byte[s.Length];
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] > 0xFF)
                {
                    throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", "String contains an invalid character");
                }

                bytes[i] = (byte)s[i];
            }
            return JsValue.String(Convert.ToBase64String(bytes));
        }, 1);
        EventTargetBinding.DefineMethod(realm, realm.GlobalObject, "atob", (_, args) =>
        {
            var s = RemoveAsciiWhitespace(ArgString(args, 0));
            if (s.Length % 4 == 1)
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", "The string to be decoded is not correctly encoded");
            }

            if (s.Length % 4 != 0)
            {
                s = s.PadRight(s.Length + (4 - s.Length % 4), '=');
            }

            try
            {
                var bytes = Convert.FromBase64String(s);
                return JsValue.String(string.Create(bytes.Length, bytes, static (span, state) =>
                {
                    for (var i = 0; i < state.Length; i++)
                    {
                        span[i] = (char)state[i];
                    }
                }));
            }
            catch (FormatException)
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", "The string to be decoded is not correctly encoded");
            }
        }, 1);
    }

    private static void InstallStructuredClone(JsRealm realm)
    {
        EventTargetBinding.DefineMethod(realm, realm.GlobalObject, "structuredClone", (_, args) =>
        {
            var seen = new Dictionary<JsObject, JsObject>(ReferenceEqualityComparer.Instance);
            return CloneValue(realm, args.Length > 0 ? args[0] : JsValue.Undefined, seen);
        }, 1);
    }

    private static JsValue CloneValue(JsRealm realm, JsValue value, Dictionary<JsObject, JsObject> seen)
    {
        if (!value.IsObject)
        {
            if (value.IsSymbol)
            {
                throw DomExceptionBinding.Throw(realm, "DataCloneError", "Symbol values cannot be cloned");
            }

            return value;
        }

        var obj = value.AsObject;
        if (seen.TryGetValue(obj, out var existing))
        {
            return JsValue.Object(existing);
        }

        if (obj is JsArrayBuffer buffer)
        {
            return JsValue.Object(NewArrayBuffer(realm, buffer.GetSpan().ToArray()));
        }

        if (obj is JsTypedArray ta)
        {
            return JsValue.Object(NewUint8Array(realm, ta.Buffer.GetSpan(ta.ByteOffset, ta.ByteLength).ToArray()));
        }

        if (obj is BlobObject blob)
        {
            return JsValue.Object(blob.CloneForRealm(realm));
        }

        if (AbstractOperations.IsCallable(value))
        {
            throw DomExceptionBinding.Throw(realm, "DataCloneError", "Function objects cannot be cloned");
        }

        JsObject clone;
        if (obj is JsArray arr)
        {
            var c = new JsArray(realm);
            seen[obj] = c;
            for (var i = 0; i < arr.Length; i++)
            {
                c.Push(CloneValue(realm, arr[i], seen));
            }

            clone = c;
        }
        else
        {
            clone = new JsObject(realm.ObjectPrototype);
            seen[obj] = clone;
            foreach (var key in obj.EnumerableKeys())
            {
                clone.DefineOwnProperty(key, PropertyDescriptor.Data(CloneValue(realm, obj.Get(key), seen), true, true, true));
            }
        }
        return JsValue.Object(clone);
    }

    private static void InstallPairIterators(JsRealm realm, JsObject proto, Func<JsValue, IReadOnlyList<(string Name, string Value)>> entriesFor)
    {
        EventTargetBinding.DefineMethod(realm, proto, "entries", (thisV, _) => PairIterator(realm, entriesFor(thisV), PairIteratorKind.KeyValue), 0);
        EventTargetBinding.DefineMethod(realm, proto, "keys", (thisV, _) => PairIterator(realm, entriesFor(thisV), PairIteratorKind.Key), 0);
        EventTargetBinding.DefineMethod(realm, proto, "values", (thisV, _) => PairIterator(realm, entriesFor(thisV), PairIteratorKind.Value), 0);
        var entriesFn = proto.GetOwnPropertyDescriptor("entries")!.Value.Value.AsObject;
        proto.DefineOwnProperty(SymbolCtor.Iterator, PropertyDescriptor.BuiltinMethod(JsValue.Object(entriesFn)));
    }

    private static JsValue PairIterator(JsRealm realm, IReadOnlyList<(string Name, string Value)> entries, PairIteratorKind kind)
    {
        var arr = new JsArray(realm);
        foreach (var (name, value) in entries)
        {
            if (kind == PairIteratorKind.Key)
            {
                arr.Push(JsValue.String(name));
            }
            else if (kind == PairIteratorKind.Value)
            {
                arr.Push(JsValue.String(value));
            }
            else
            {
                arr.Push(JsValue.Object(new JsArray(realm, new[] { JsValue.String(name), JsValue.String(value) })));
            }
        }
        return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(arr), ArrayIteratorKind.Value);
    }

    private static void InstallFormDataIterators(JsRealm realm, JsObject proto)
    {
        EventTargetBinding.DefineMethod(realm, proto, "entries", (thisV, _) => FormDataIterator(realm, FormDataObject.Require(realm, thisV).Entries, PairIteratorKind.KeyValue), 0);
        EventTargetBinding.DefineMethod(realm, proto, "keys", (thisV, _) => FormDataIterator(realm, FormDataObject.Require(realm, thisV).Entries, PairIteratorKind.Key), 0);
        EventTargetBinding.DefineMethod(realm, proto, "values", (thisV, _) => FormDataIterator(realm, FormDataObject.Require(realm, thisV).Entries, PairIteratorKind.Value), 0);
        var entriesFn = proto.GetOwnPropertyDescriptor("entries")!.Value.Value.AsObject;
        proto.DefineOwnProperty(SymbolCtor.Iterator, PropertyDescriptor.BuiltinMethod(JsValue.Object(entriesFn)));
    }

    private static JsValue FormDataIterator(JsRealm realm, IReadOnlyList<FormDataEntry> entries, PairIteratorKind kind)
    {
        var arr = new JsArray(realm);
        foreach (var entry in entries)
        {
            if (kind == PairIteratorKind.Key)
            {
                arr.Push(JsValue.String(entry.Name));
            }
            else if (kind == PairIteratorKind.Value)
            {
                arr.Push(entry.Value);
            }
            else
            {
                arr.Push(JsValue.Object(new JsArray(realm, new[] { JsValue.String(entry.Name), entry.Value })));
            }
        }
        return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(arr), ArrayIteratorKind.Value);
    }

    private static byte[] BytesFromBlobParts(JsRealm realm, JsValue parts)
    {
        if (!parts.IsObject)
        {
            return Array.Empty<byte>();
        }

        var obj = parts.AsObject;
        var len = LengthOf(obj);
        using var ms = new MemoryStream();
        for (var i = 0; i < len; i++)
        {
            var bytes = BlobPartBytes(realm, obj.Get(i.ToString(CultureInfo.InvariantCulture)));
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] BlobPartBytes(JsRealm realm, JsValue value)
    {
        if (value.IsObject)
        {
            if (value.AsObject is BlobObject blob)
            {
                return blob.Bytes.ToArray();
            }

            if (value.AsObject is JsArrayBuffer or JsTypedArray)
            {
                return BytesFromBufferSource(realm, value);
            }
        }
        return Encoding.UTF8.GetBytes(JsValue.ToStringValue(value));
    }

    internal static byte[] BytesFromBufferSource(JsRealm realm, JsValue value)
    {
        if (!value.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Expected BufferSource"));
        }

        if (value.AsObject is JsArrayBuffer ab)
        {
            return ab.GetSpan().ToArray();
        }

        if (value.AsObject is JsTypedArray ta)
        {
            return ta.Buffer.GetSpan(ta.ByteOffset, ta.ByteLength).ToArray();
        }

        throw new JsThrow(realm.NewTypeError("Expected BufferSource"));
    }

    internal static JsArrayBuffer NewArrayBuffer(JsRealm realm, byte[] bytes)
    {
        var buffer = new JsArrayBuffer(realm.ArrayBufferPrototype, bytes.Length);
        bytes.CopyTo(buffer.GetSpan());
        return buffer;
    }

    internal static JsTypedArray NewUint8Array(JsRealm realm, byte[] bytes)
    {
        var proto = realm.GlobalObject.Get("Uint8Array").AsObject.Get("prototype").AsObject;
        var buffer = NewArrayBuffer(realm, bytes);
        return new JsTypedArray(proto, JsTypedArrayKind.Uint8, buffer, 0, bytes.Length);
    }

    internal static byte[] SerializeFormDataMultipart(FormDataObject form, out string contentType)
    {
        var boundary = "----starling-formdata-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        contentType = "multipart/form-data; boundary=" + boundary;
        using var ms = new MemoryStream();
        foreach (var entry in form.Entries)
        {
            WriteAscii(ms, "--" + boundary + "\r\n");
            var disposition = "Content-Disposition: form-data; name=\"" + EscapeQuoted(entry.Name) + "\"";
            if (entry.Value.IsObject && entry.Value.AsObject is BlobObject blob)
            {
                var filename = entry.FileName ?? (blob is FileObject file ? file.Name : "blob");
                WriteAscii(ms, disposition + "; filename=\"" + EscapeQuoted(filename) + "\"\r\n");
                if (blob.Type.Length > 0)
                {
                    WriteAscii(ms, "Content-Type: " + blob.Type + "\r\n");
                }

                WriteAscii(ms, "\r\n");
                ms.Write(blob.Bytes, 0, blob.Bytes.Length);
                WriteAscii(ms, "\r\n");
            }
            else
            {
                WriteAscii(ms, disposition + "\r\n\r\n");
                var bytes = Encoding.UTF8.GetBytes(JsValue.ToStringValue(entry.Value));
                ms.Write(bytes, 0, bytes.Length);
                WriteAscii(ms, "\r\n");
            }
        }
        WriteAscii(ms, "--" + boundary + "--\r\n");
        form.LastSerializedBytes = ms.ToArray();
        return form.LastSerializedBytes;
    }

    internal static FormDataObject ParseUrlEncodedFormData(JsRealm realm, JsObject proto, string body)
    {
        var form = new FormDataObject(proto);
        foreach (var (name, value) in UrlSearchParamsObject.ParsePairs(body))
        {
            form.AppendString(name, value);
        }

        return form;
    }

    private static int ClampSlice(JsValue value, int size, int defaultValue)
    {
        if (value.IsUndefined)
        {
            return defaultValue;
        }

        var n = (int)Math.Truncate(JsValue.ToNumber(value));
        if (n < 0)
        {
            return Math.Max(size + n, 0);
        }

        return Math.Min(n, size);
    }

    private static int LengthOf(JsObject obj)
    {
        var len = obj.Get("length");
        return len.IsNumber ? Math.Max((int)len.AsNumber, 0) : 0;
    }

    private static string ArgString(JsValue[] args, int index)
        => args.Length > index ? JsValue.ToStringValue(args[index]) : "";

    private static void DefineCtor(JsRealm realm, JsObject ctor, JsObject proto, string name)
    {
        ctor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.Object(proto), false, false, false));
        proto.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
        realm.GlobalObject.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static string NormalizeMimeType(string type)
    {
        foreach (var ch in type)
        {
            if (ch < 0x20 || ch > 0x7E)
            {
                return "";
            }
        }

        return type.ToLowerInvariant();
    }

    private static string RemoveAsciiWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is not ('\t' or '\n' or '\f' or '\r' or ' '))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string EscapeQuoted(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void WriteAscii(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private enum PairIteratorKind { KeyValue, Key, Value }
}

internal sealed class UrlSearchParamsObject : JsObject
{
    private readonly Action<string>? _onChange;
    private readonly List<(string Name, string Value)> _entries = new();

    public UrlSearchParamsObject(JsObject? proto, Action<string>? onChange = null) : base(proto)
    {
        _onChange = onChange;
    }

    public IReadOnlyList<(string Name, string Value)> Entries => _entries;

    public void Populate(JsRealm realm, JsValue init)
    {
        if (init.IsUndefined || init.IsNull)
        {
            return;
        }

        if (init.IsString)
        {
            foreach (var pair in ParsePairs(init.AsString.TrimStart('?')))
            {
                _entries.Add(pair);
            }

            Notify();
            return;
        }
        if (!init.IsObject)
        {
            return;
        }

        if (init.AsObject is UrlSearchParamsObject other)
        {
            _entries.AddRange(other._entries);
            Notify();
            return;
        }
        var obj = init.AsObject;
        if (obj is JsArray arr)
        {
            for (var i = 0; i < arr.Length; i++)
            {
                var pair = arr[i];
                if (!pair.IsObject)
                {
                    continue;
                }

                Append(JsValue.ToStringValue(pair.AsObject.Get("0")), JsValue.ToStringValue(pair.AsObject.Get("1")));
            }
            return;
        }
        foreach (var key in obj.EnumerableKeys())
        {
            Append(key, JsValue.ToStringValue(obj.Get(key)));
        }
    }

    public void Append(string name, string value)
    {
        _entries.Add((name, value));
        Notify();
    }

    public void DeleteEntry(string name)
    {
        _entries.RemoveAll(e => e.Name == name);
        Notify();
    }

    public string? GetEntry(string name)
    {
        foreach (var entry in _entries)
        {
            if (entry.Name == name)
            {
                return entry.Value;
            }
        }

        return null;
    }

    public IEnumerable<string> GetAll(string name)
    {
        foreach (var entry in _entries)
        {
            if (entry.Name == name)
            {
                yield return entry.Value;
            }
        }
    }

    public bool HasEntry(string name)
    {
        foreach (var entry in _entries)
        {
            if (entry.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    public void Set(string name, string value)
    {
        var inserted = false;
        for (var i = 0; i < _entries.Count;)
        {
            if (_entries[i].Name != name) { i++; continue; }
            if (!inserted)
            {
                _entries[i] = (name, value);
                inserted = true;
                i++;
            }
            else
            {
                _entries.RemoveAt(i);
            }
        }
        if (!inserted)
        {
            _entries.Add((name, value));
        }

        Notify();
    }

    public void Sort()
    {
        _entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        Notify();
    }

    public void AppendSilently(string name, string value) => _entries.Add((name, value));

    public void DeleteAllSilently() => _entries.Clear();

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in _entries)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(Encode(name)).Append('=').Append(Encode(value));
        }
        return sb.ToString();
    }

    public static IReadOnlyList<(string Name, string Value)> ParsePairs(string query)
    {
        var entries = new List<(string Name, string Value)>();
        if (query.Length == 0)
        {
            return entries;
        }

        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            entries.Add((Decode(name), Decode(value)));
        }
        return entries;
    }

    public static UrlSearchParamsObject Require(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is UrlSearchParamsObject p)
        {
            return p;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a URLSearchParams"));
    }

    private void Notify() => _onChange?.Invoke(Serialize());

    private static string Encode(string value)
        => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static string Decode(string value)
        => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
}

internal sealed class UrlObject : JsObject
{
    private readonly JsRealm _realm;
    private readonly JsObject? _searchParamsProto;
    private Uri _uri;

    public UrlObject(JsObject? proto, JsRealm realm, string input, string? baseUrl) : base(proto)
    {
        _realm = realm;
        _searchParamsProto = realm.GlobalObject.Get("URLSearchParams").AsObject.Get("prototype").AsObject;
        _uri = Parse(input, baseUrl, realm);
        SearchParams = new UrlSearchParamsObject(_searchParamsProto, q => SetSearch(q.Length == 0 ? "" : "?" + q));
        SyncSearchParamsFromUri();
    }

    public UrlSearchParamsObject SearchParams { get; }
    public string Href => _uri.AbsoluteUri;
    public string Origin => _uri.IsDefaultPort ? $"{_uri.Scheme}://{_uri.Host}" : $"{_uri.Scheme}://{_uri.Host}:{_uri.Port}";
    public string Protocol => _uri.Scheme + ":";
    public string Host => _uri.IsDefaultPort ? _uri.Host : _uri.Host + ":" + _uri.Port.ToString(CultureInfo.InvariantCulture);
    public string Hostname => _uri.Host;
    public string Port => _uri.IsDefaultPort ? "" : _uri.Port.ToString(CultureInfo.InvariantCulture);
    public string Pathname => _uri.AbsolutePath;
    public string Search => _uri.Query;
    public string Hash => _uri.Fragment;

    public void SetHref(string href)
    {
        _uri = Parse(href, null, _realm);
        SyncSearchParamsFromUri();
    }
    public void SetProtocol(string value) => Mutate(b => b.Scheme = value.TrimEnd(':'));
    public void SetHost(string value)
    {
        var parts = value.Split(':', 2);
        Mutate(b =>
        {
            b.Host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            {
                b.Port = p;
            }
        });
    }
    public void SetHostname(string value) => Mutate(b => b.Host = value);
    public void SetPort(string value) => Mutate(b =>
    {
        if (value.Length == 0)
        {
            b.Port = -1;
            return;
        }
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 0 and <= 65535)
        {
            b.Port = port;
        }
    });
    public void SetPathname(string value) => Mutate(b => b.Path = value.Length == 0 || value[0] == '/' ? value : "/" + value);
    public void SetSearch(string value)
    {
        Mutate(b => b.Query = value.TrimStart('?'));
        SyncSearchParamsFromUri();
    }
    public void SetHash(string value) => Mutate(b => b.Fragment = value.TrimStart('#'));

    public static UrlObject Require(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is UrlObject u)
        {
            return u;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a URL"));
    }

    private static Uri Parse(string input, string? baseUrl, JsRealm realm)
    {
        if (baseUrl is null)
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var abs))
            {
                return abs;
            }
        }
        else if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, input, out var rel))
        {
            return rel;
        }
        throw new JsThrow(realm.NewTypeError("Invalid URL"));
    }

    private void Mutate(Action<UriBuilder> change)
    {
        var builder = new UriBuilder(_uri);
        change(builder);
        _uri = builder.Uri;
    }

    private void SyncSearchParamsFromUri()
    {
        SearchParams.DeleteAllSilently();
        foreach (var pair in UrlSearchParamsObject.ParsePairs(_uri.Query.TrimStart('?')))
        {
            SearchParams.AppendSilently(pair.Name, pair.Value);
        }
    }
}

internal class BlobObject : JsObject
{
    public BlobObject(JsObject? proto, byte[] bytes, string type) : base(proto)
    {
        Bytes = bytes;
        Type = type;
    }

    public byte[] Bytes { get; }
    public string Type { get; }

    public virtual BlobObject CloneForRealm(JsRealm realm)
        => new(realm.GlobalObject.Get("Blob").AsObject.Get("prototype").AsObject, Bytes.ToArray(), Type);

    public static BlobObject Require(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is BlobObject b)
        {
            return b;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a Blob"));
    }
}

internal sealed class FileObject : BlobObject
{
    public FileObject(JsObject? proto, byte[] bytes, string type, string name, long lastModified) : base(proto, bytes, type)
    {
        Name = name;
        LastModified = lastModified;
    }

    public string Name { get; }
    public long LastModified { get; }

    public override BlobObject CloneForRealm(JsRealm realm)
        => new FileObject(realm.GlobalObject.Get("File").AsObject.Get("prototype").AsObject, Bytes.ToArray(), Type, Name, LastModified);

    public static FileObject RequireFile(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is FileObject f)
        {
            return f;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a File"));
    }
}

internal sealed record FormDataEntry(string Name, JsValue Value, string? FileName);

internal sealed class FormDataObject : JsObject
{
    private readonly List<FormDataEntry> _entries = new();

    public FormDataObject(JsObject? proto) : base(proto) { }

    public IReadOnlyList<FormDataEntry> Entries => _entries;
    public byte[]? LastSerializedBytes { get; set; }

    public void Append(JsRealm realm, string name, JsValue value, JsValue fileName)
    {
        string? fn = null;
        JsValue stored;
        if (value.IsObject && value.AsObject is BlobObject)
        {
            stored = value;
            if (!fileName.IsUndefined)
            {
                fn = JsValue.ToStringValue(fileName);
            }
        }
        else
        {
            stored = JsValue.String(JsValue.ToStringValue(value));
        }
        _entries.Add(new FormDataEntry(name, stored, fn));
    }

    public void AppendString(string name, string value) => _entries.Add(new FormDataEntry(name, JsValue.String(value), null));
    public void AppendSilently(string name, string value) => AppendString(name, value);
    public void DeleteAllSilently() => _entries.Clear();
    public void DeleteEntry(string name) => _entries.RemoveAll(e => e.Name == name);
    public bool HasEntry(string name) => _entries.Exists(e => e.Name == name);

    public JsValue? GetEntry(string name)
    {
        foreach (var entry in _entries)
        {
            if (entry.Name == name)
            {
                return entry.Value;
            }
        }

        return null;
    }

    public IEnumerable<JsValue> GetAll(string name)
    {
        foreach (var entry in _entries)
        {
            if (entry.Name == name)
            {
                yield return entry.Value;
            }
        }
    }

    public static FormDataObject Require(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is FormDataObject f)
        {
            return f;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a FormData"));
    }
}

internal sealed class TextDecoderObject : JsObject
{
    public TextDecoderObject(JsRealm realm, JsObject? proto, string label, bool fatal, bool ignoreBom) : base(proto)
    {
        EncodingName = Normalize(label);
        Fatal = fatal;
        IgnoreBom = ignoreBom;
        Encoding = EncodingName switch
        {
            "utf-8" => new UTF8Encoding(false, fatal),
            "utf-16le" => new UnicodeEncoding(false, false, fatal),
            "utf-16be" => new UnicodeEncoding(true, false, fatal),
            _ => throw new JsThrow(realm.NewRangeError("Unsupported TextDecoder encoding")),
        };
    }

    public string EncodingName { get; }
    public bool Fatal { get; }
    public bool IgnoreBom { get; }
    public Encoding Encoding { get; }

    public static TextDecoderObject Require(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is TextDecoderObject d)
        {
            return d;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a TextDecoder"));
    }

    private static string Normalize(string label)
    {
        var lower = label.Trim().ToLowerInvariant();
        return lower switch
        {
            "" or "unicode-1-1-utf-8" or "unicode11utf8" or "unicode20utf8" or "utf-8" or "utf8" or "x-unicode20utf8" => "utf-8",
            "utf-16" or "utf-16le" => "utf-16le",
            "utf-16be" => "utf-16be",
            _ => lower,
        };
    }
}
