﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Converters;

namespace System.Text.Json
{
    [DebuggerDisplay("PropertyInfo={PropertyInfo}, Element={ElementClassInfo}")]
    internal abstract class JsonPropertyInfo
    {
        // Cache the converters so they don't get created for every enumerable property.
        private static readonly JsonEnumerableConverter s_jsonArrayConverter = new DefaultArrayConverter();
        private static readonly JsonEnumerableConverter s_jsonICollectionConverter = new DefaultICollectionConverter();
        private static readonly JsonDictionaryConverter s_jsonIDictionaryConverter = new DefaultIDictionaryConverter();
        private static readonly JsonEnumerableConverter s_jsonImmutableEnumerableConverter = new DefaultImmutableEnumerableConverter();
        private static readonly JsonDictionaryConverter s_jsonImmutableDictionaryConverter = new DefaultImmutableDictionaryConverter();

        public static readonly JsonPropertyInfo s_missingProperty = new JsonPropertyInfoNotNullable<object, object, object, object>();

        private Type _elementType;
        private JsonClassInfo _elementClassInfo;
        private JsonClassInfo _runtimeClassInfo;

        public bool CanBeNull { get; private set; }

        public ClassType ClassType;

        // After the property is added, clear any state not used later.
        public void ClearUnusedValuesAfterAdd()
        {
            NameAsString = null;
            NameUsedToCompareAsString = null;
        }

        public abstract JsonConverter ConverterBase { get; set; }

        // Copy any settings defined at run-time to the new property.
        public void CopyRuntimeSettingsTo(JsonPropertyInfo other)
        {
            other.Name = Name;
            other.NameUsedToCompare = NameUsedToCompare;
            other.EscapedName = EscapedName;
        }

        public abstract IList CreateConverterList();

        public abstract IEnumerable CreateIEnumerableInstance(Type parentType, IList sourceList, string jsonPath, JsonSerializerOptions options);

        public abstract IDictionary CreateIDictionaryInstance(Type parentType, IDictionary sourceDictionary, string jsonPath, JsonSerializerOptions options);

        // Create a property that is ignored at run-time. It uses the same type (typeof(sbyte)) to help
        // prevent issues with unsupported types and helps ensure we don't accidently (de)serialize it.
        public static JsonPropertyInfo CreateIgnoredPropertyPlaceholder(PropertyInfo propertyInfo, JsonSerializerOptions options)
        {
            JsonPropertyInfo jsonPropertyInfo = new JsonPropertyInfoNotNullable<sbyte, sbyte, sbyte, sbyte>();
            jsonPropertyInfo.Options = options;
            jsonPropertyInfo.PropertyInfo = propertyInfo;
            jsonPropertyInfo.DeterminePropertyName();

            Debug.Assert(!jsonPropertyInfo.ShouldDeserialize);
            Debug.Assert(!jsonPropertyInfo.ShouldSerialize);

            return jsonPropertyInfo;
        }

        public abstract IEnumerable CreateImmutableCollectionInstance(Type collectionType, string delegateKey, IList sourceList, string propertyPath, JsonSerializerOptions options);

        public abstract IDictionary CreateImmutableDictionaryInstance(Type collectionType, string delegateKey, IDictionary sourceDictionary, string propertyPath, JsonSerializerOptions options);

        public Type DeclaredPropertyType { get; private set; }

        private void DeterminePropertyName()
        {
            if (PropertyInfo == null)
            {
                return;
            }

            JsonPropertyNameAttribute nameAttribute = GetAttribute<JsonPropertyNameAttribute>(PropertyInfo);
            if (nameAttribute != null)
            {
                string name = nameAttribute.Name;
                if (name == null)
                {
                    ThrowHelper.ThrowInvalidOperationException_SerializerPropertyNameNull(ParentClassType, this);
                }

                NameAsString = name;
            }
            else if (Options.PropertyNamingPolicy != null)
            {
                string name = Options.PropertyNamingPolicy.ConvertName(PropertyInfo.Name);
                if (name == null)
                {
                    ThrowHelper.ThrowInvalidOperationException_SerializerPropertyNameNull(ParentClassType, this);
                }

                NameAsString = name;
            }
            else
            {
                NameAsString = PropertyInfo.Name;
            }

            Debug.Assert(NameAsString != null);

            // At this point propertyName is valid UTF16, so just call the simple UTF16->UTF8 encoder.
            Name = Encoding.UTF8.GetBytes(NameAsString);

            // Set the compare name.
            if (Options.PropertyNameCaseInsensitive)
            {
                NameUsedToCompareAsString = NameAsString.ToUpperInvariant();
                NameUsedToCompare = Encoding.UTF8.GetBytes(NameUsedToCompareAsString);
            }
            else
            {
                NameUsedToCompareAsString = NameAsString;
                NameUsedToCompare = Name;
            }

            // Cache the escaped name.
            EscapedName = JsonEncodedText.Encode(Name);
        }

        private void DetermineSerializationCapabilities()
        {
            if (ClassType != ClassType.Enumerable &&
                ClassType != ClassType.Dictionary &&
                ClassType != ClassType.IDictionaryConstructible)
            {
                // We serialize if there is a getter + not ignoring readonly properties.
                ShouldSerialize = HasGetter && (HasSetter || !Options.IgnoreReadOnlyProperties);

                // We deserialize if there is a setter. 
                ShouldDeserialize = HasSetter;
            }
            else
            {
                if (HasGetter)
                {
                    if (HasSetter)
                    {
                        ShouldDeserialize = true;
                    }
                    else if (!RuntimePropertyType.IsArray &&
                        (typeof(IList).IsAssignableFrom(RuntimePropertyType) || typeof(IDictionary).IsAssignableFrom(RuntimePropertyType)))
                    {
                        ShouldDeserialize = true;
                    }
                }
                //else if (HasSetter)
                //{
                //    // todo: Special case where there is no getter but a setter (and an EnumerableConverter)
                //}

                if (ShouldDeserialize)
                {
                    ShouldSerialize = HasGetter;

                    if (RuntimePropertyType.IsArray)
                    {
                        EnumerableConverter = s_jsonArrayConverter;
                    }
                    else if (ClassType == ClassType.IDictionaryConstructible)
                    {
                        if (RuntimePropertyType.FullName.StartsWith(JsonClassInfo.ImmutableNamespaceName))
                        {
                            DefaultImmutableDictionaryConverter.RegisterImmutableDictionary(
                                RuntimePropertyType, JsonClassInfo.GetElementType(RuntimePropertyType, ParentClassType, PropertyInfo, Options), Options);

                            DictionaryConverter = s_jsonImmutableDictionaryConverter;
                        }
                        else if (JsonClassInfo.IsDeserializedByConstructingWithIDictionary(RuntimePropertyType))
                        {
                            DictionaryConverter = s_jsonIDictionaryConverter;
                        }
                    }
                    else if (typeof(IEnumerable).IsAssignableFrom(RuntimePropertyType))
                    {
                        if (JsonClassInfo.IsDeserializedByConstructingWithIList(RuntimePropertyType) ||
                            (!typeof(IList).IsAssignableFrom(RuntimePropertyType) && 
                            JsonClassInfo.HasConstructorThatTakesGenericIEnumerable(RuntimePropertyType, Options)))
                        {
                            EnumerableConverter = s_jsonICollectionConverter;
                        }
                        else if (RuntimePropertyType.IsGenericType &&
                            RuntimePropertyType.FullName.StartsWith(JsonClassInfo.ImmutableNamespaceName) &&
                            RuntimePropertyType.GetGenericArguments().Length == 1)
                        {
                            DefaultImmutableEnumerableConverter.RegisterImmutableCollection(RuntimePropertyType,
                                JsonClassInfo.GetElementType(RuntimePropertyType, ParentClassType, PropertyInfo, Options), Options);
                            EnumerableConverter = s_jsonImmutableEnumerableConverter;
                        }
                    }
                }
                else
                {
                    ShouldSerialize = HasGetter && !Options.IgnoreReadOnlyProperties;
                }
            }
        }

        public JsonDictionaryConverter DictionaryConverter { get; private set; }

        /// <summary>
        /// Return the JsonClassInfo for the element type, or null if the the property is not an enumerable or dictionary.
        /// </summary>
        public JsonClassInfo ElementClassInfo
        {
            get
            {
                if (_elementClassInfo == null && _elementType != null)
                {
                    Debug.Assert(ClassType == ClassType.Enumerable ||
                        ClassType == ClassType.Dictionary ||
                        ClassType == ClassType.IDictionaryConstructible);
                    _elementClassInfo = Options.GetOrAddClass(_elementType);
                }

                return _elementClassInfo;
            }
        }

        public JsonEnumerableConverter EnumerableConverter { get; private set; }

        // The escaped name passed to the writer.
        public JsonEncodedText? EscapedName { get; private set; }

        public static TAttribute GetAttribute<TAttribute>(PropertyInfo propertyInfo) where TAttribute : Attribute
        {
            return (TAttribute)propertyInfo?.GetCustomAttribute(typeof(TAttribute), inherit: false);
        }

        public abstract Type GetDictionaryConcreteType();

        public abstract Type GetConcreteType(Type type);

        private static void GetOriginalValues(ref Utf8JsonReader reader, out JsonTokenType tokenType, out int depth)
        {
            tokenType = reader.TokenType;
            depth = reader.CurrentDepth;
        }

        public virtual void GetPolicies()
        {
            DetermineSerializationCapabilities();
            DeterminePropertyName();
            IgnoreNullValues = Options.IgnoreNullValues;
        }

        public abstract object GetValueAsObject(object obj);

        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }

        public virtual void Initialize(
            Type parentClassType,
            Type declaredPropertyType,
            Type runtimePropertyType,
            PropertyInfo propertyInfo,
            Type elementType,
            JsonConverter converter,
            JsonSerializerOptions options)
        {
            ParentClassType = parentClassType;
            DeclaredPropertyType = declaredPropertyType;
            RuntimePropertyType = runtimePropertyType;
            PropertyInfo = propertyInfo;
            _elementType = elementType;
            Options = options;
            IsNullableType = runtimePropertyType.IsGenericType && runtimePropertyType.GetGenericTypeDefinition() == typeof(Nullable<>);
            CanBeNull = IsNullableType || !runtimePropertyType.IsValueType;

            if (converter != null)
            {
                ConverterBase = converter;

                // Avoid calling GetClassType since it will re-ask if there is a converter which is slow.
                if (runtimePropertyType == typeof(object))
                {
                    ClassType = ClassType.Unknown;
                }
                else
                {
                    ClassType = ClassType.Value;
                }
            }
            else
            {
                ClassType = JsonClassInfo.GetClassType(runtimePropertyType, options);
            }
        }

        public bool IgnoreNullValues { get; private set; }

        public bool IsNullableType { get; private set; }

        public bool IsPropertyPolicy { get; protected set; }

        // The name from a Json value. This is cached for performance on first deserialize.
        public byte[] JsonPropertyName { get; set; }

        // The name of the property with any casing policy or the name specified from JsonPropertyNameAttribute.
        public byte[] Name { get; private set; }
        public string NameAsString { get; private set; }

        // Used to support case-insensitive comparison
        public byte[] NameUsedToCompare { get; private set; }
        public string NameUsedToCompareAsString { get; private set; }

        // Options can be referenced here since all JsonPropertyInfos originate from a JsonClassInfo that is cached on JsonSerializerOptions.
        protected JsonSerializerOptions Options { get; set; }

        protected abstract void OnRead(JsonTokenType tokenType, ref ReadStack state, ref Utf8JsonReader reader);
        protected abstract void OnReadEnumerable(JsonTokenType tokenType, ref ReadStack state, ref Utf8JsonReader reader);
        protected abstract void OnWrite(ref WriteStackFrame current, Utf8JsonWriter writer);
        protected virtual void OnWriteDictionary(ref WriteStackFrame current, Utf8JsonWriter writer) { }
        protected abstract void OnWriteEnumerable(ref WriteStackFrame current, Utf8JsonWriter writer);

        public Type ParentClassType { get; private set; }

        public PropertyInfo PropertyInfo { get; private set; }

        public void Read(JsonTokenType tokenType, ref ReadStack state, ref Utf8JsonReader reader)
        {
            Debug.Assert(ShouldDeserialize);

            if (ElementClassInfo != null)
            {
                // Forward the setter to the value-based JsonPropertyInfo.
                JsonPropertyInfo propertyInfo = ElementClassInfo.GetPolicyProperty();
                propertyInfo.OnReadEnumerable(tokenType, ref state, ref reader);
            }
            else
            {
                GetOriginalValues(ref reader, out JsonTokenType originalTokenType, out int originalDepth);
                OnRead(tokenType, ref state, ref reader);
                VerifyRead(originalTokenType, originalDepth, ref state, ref reader);
            }
        }

        public void ReadEnumerable(JsonTokenType tokenType, ref ReadStack state, ref Utf8JsonReader reader)
        {
            Debug.Assert(ShouldDeserialize);

            GetOriginalValues(ref reader, out JsonTokenType originalTokenType, out int originalDepth);
            OnReadEnumerable(tokenType, ref state, ref reader);
            VerifyRead(originalTokenType, originalDepth, ref state, ref reader);
        }

        public JsonClassInfo RuntimeClassInfo
        {
            get
            {
                if (_runtimeClassInfo == null)
                {
                    _runtimeClassInfo = Options.GetOrAddClass(RuntimePropertyType);
                }

                return _runtimeClassInfo;
            }
        }

        public Type RuntimePropertyType { get; private set; }

        public abstract void SetValueAsObject(object obj, object value);

        public bool ShouldSerialize { get; private set; }
        public bool ShouldDeserialize { get; private set; }

        private void VerifyRead(JsonTokenType originalTokenType, int originalDepth, ref ReadStack state, ref Utf8JsonReader reader)
        {
            // We don't have a single call to ThrowHelper since the line number captured during throw may be useful for diagnostics.
            switch (originalTokenType)
            {
                case JsonTokenType.StartArray:
                    if (reader.TokenType != JsonTokenType.EndArray)
                    {
                        // todo issue #38550 blocking this: originalDepth != reader.CurrentDepth + 1
                        ThrowHelper.ThrowJsonException_SerializationConverterRead(reader, state.JsonPath, ConverterBase.ToString());
                    }
                    break;

                case JsonTokenType.StartObject:
                    if (reader.TokenType != JsonTokenType.EndObject)
                    {
                        // todo issue #38550 blocking this: originalDepth != reader.CurrentDepth + 1
                        ThrowHelper.ThrowJsonException_SerializationConverterRead(reader, state.JsonPath, ConverterBase.ToString());
                    }
                    break;

                default:
                    // Reading a single property value.
                    if (reader.TokenType != originalTokenType)
                    {
                        // todo issue #38550 blocking this: originalDepth != reader.CurrentDepth + 1
                        ThrowHelper.ThrowJsonException_SerializationConverterRead(reader, state.JsonPath, ConverterBase.ToString());
                    }

                    break;
            }
        }

        private void VerifyWrite(int originalDepth, ref WriteStack state, ref Utf8JsonWriter writer)
        {
            // todo issue #38550 blocking this: originalDepth != reader.CurrentDepth
            if (originalDepth != writer.CurrentDepth)
            {
                ThrowHelper.ThrowJsonException_SerializationConverterWrite(state.PropertyPath, ConverterBase.ToString());
            }
        }

        public void Write(ref WriteStack state, Utf8JsonWriter writer)
        {
            Debug.Assert(ShouldSerialize);

            if (state.Current.Enumerator != null)
            {
                // Forward the setter to the value-based JsonPropertyInfo.
                JsonPropertyInfo propertyInfo = ElementClassInfo.GetPolicyProperty();
                propertyInfo.OnWriteEnumerable(ref state.Current, writer);
            }
            else
            {
                int originalDepth = writer.CurrentDepth;
                OnWrite(ref state.Current, writer);
                VerifyWrite(originalDepth, ref state, ref writer);
            }
        }

        public void WriteDictionary(ref WriteStack state, Utf8JsonWriter writer)
        {
            Debug.Assert(ShouldSerialize);

            int originalDepth = writer.CurrentDepth;
            OnWriteDictionary(ref state.Current, writer);
            VerifyWrite(originalDepth, ref state, ref writer);
        }

        public void WriteEnumerable(ref WriteStack state, Utf8JsonWriter writer)
        {
            Debug.Assert(ShouldSerialize);

            int originalDepth = writer.CurrentDepth;
            OnWriteEnumerable(ref state.Current, writer);
            VerifyWrite(originalDepth, ref state, ref writer);
        }
    }
}
