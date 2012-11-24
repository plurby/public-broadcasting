﻿using FastMember;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace PublicBroadcasting.Impl
{
    internal class POCOMapper
    {
        protected Func<object, object> Mapper { get; set; }

        protected POCOMapper() { }

        internal POCOMapper(Func<object, object> map)
        {
            Mapper = map;
        }

        internal Func<object, object> GetMapper()
        {
            return Mapper;
        }
    }

    internal class PromisedPOCOMapper : POCOMapper
    {
        internal PromisedPOCOMapper() { }

        internal void Fulfil(Func<object, object> mapper)
        {
            Mapper = mapper;
        }
    }

    internal class POCOMapper<From, To>
    {
        private static readonly POCOMapper Mapper;
        private static readonly PromisedPOCOMapper PromisedMapper;

        static POCOMapper()
        {
            PromisedMapper = new PromisedPOCOMapper();

            var mapper = GetMapper();
            PromisedMapper.Fulfil(mapper.GetMapper());

            Mapper = mapper;
        }

        private static POCOMapper GetDictionaryMapper()
        {
            var toDict = typeof(To);
            var fromDict = typeof(From);

            if (!(toDict.IsGenericType && toDict.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
                toDict = toDict.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            }

            if(!(fromDict.IsGenericType && fromDict.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
                fromDict = fromDict.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            }
            
            var toKeyType = toDict.GetGenericArguments()[0];
            var toValType = toDict.GetGenericArguments()[1];

            var fromKeyType = fromDict.GetGenericArguments()[0];
            var fromValType = fromDict.GetGenericArguments()[1];

            var keyMapper = typeof(POCOMapper<,>).MakeGenericType(fromKeyType, toKeyType).GetMethod("Get");
            var keyMap = (POCOMapper)keyMapper.Invoke(null, new object[0]);

            var valMapper = typeof(POCOMapper<,>).MakeGenericType(fromValType, toValType).GetMethod("Get");
            var valMap = (POCOMapper)valMapper.Invoke(null, new object[0]);

            //var newDictType = typeof(Dictionary<,>).MakeGenericType(toKeyType, toValType);
            var newDictType = typeof(To);

            if (newDictType.IsInterface)
            {
                newDictType = typeof(Dictionary<,>).MakeGenericType(toKeyType, toValType);
            }

            var newDictCons = newDictType.GetConstructor(new Type[0]);

            if (newDictCons == null)
            {
                throw new Exception(typeof(To).FullName + " doesn't have a parameterless constructor");
            }

            return
                new POCOMapper(
                    dictX =>
                    {
                        if (dictX == null) return null;

                        var keyFunc = keyMap.GetMapper();
                        var valFunc = valMap.GetMapper();

                        var ret = (IDictionary)newDictCons.Invoke(new object[0]);

                        var asDict = (IDictionary)dictX;

                        var e = asDict.Keys.GetEnumerator();

                        while (e.MoveNext())
                        {
                            var key = e.Current;
                            var mKey = keyFunc(key);

                            var val = asDict[key];
                            var mVal = valFunc(val);

                            ret.Add(mKey, mVal);
                        }

                        return ret;
                    }
                );
        }

        private static POCOMapper GetListMapper()
        {
            bool toToArray = false;
            Type toListType, fromListType;

            if (typeof(To).IsArray)
            {
                toListType = typeof(To).GetElementType();
                toToArray = true;
            }
            else
            {
                toListType = typeof(To).GetGenericArguments()[0];
            }

            if (typeof(From).IsArray)
            {
                fromListType = typeof(From).GetElementType();
            }
            else
            {
                fromListType = typeof(From).GetGenericArguments()[0];
            }

            var mapper = typeof(POCOMapper<,>).MakeGenericType(fromListType, toListType).GetMethod("Get");
            var map = (POCOMapper)mapper.Invoke(null, new object[0]);

            var newListType = typeof(List<>).MakeGenericType(toListType);
            var newListCons = newListType.GetConstructor(new Type[0]);

            return
                new POCOMapper(
                    listX =>
                    {
                        if (listX == null) return null;

                        var func = map.GetMapper();

                        var ret = (IList)newListCons.Invoke(new object[0]);

                        var asEnum = (IEnumerable)listX;

                        foreach (var o in asEnum)
                        {
                            var mapped = func(o);

                            ret.Add(mapped);
                        }

                        if (toToArray)
                        {
                            return ret.ToArray(toListType);
                        }

                        return ret;
                    }
                );
        }

        private static Func<object, object> Lookup(Dictionary<string, POCOMapper> lookup, string term)
        {
            return lookup[term].GetMapper();
        }

        private static Func<From, Dictionary<string, POCOMapper>, To> BuildRefRefTypeMapper(Dictionary<string, POCOMapper> members)
        {
            var tTo = typeof(To);
            var tFrom = typeof(From);

            var cons = tTo.GetConstructor(new Type[0]);
            if (cons == null) throw new Exception("No parameterless constructor found for " + tTo.FullName);

            var lookup = typeof(POCOMapper<From, To>).GetMethod("Lookup", BindingFlags.Static | BindingFlags.NonPublic);

            var invoke = typeof(Func<object, object>).GetMethod("Invoke");

            var dynMethod = new DynamicMethod("POCOMapper" + Guid.NewGuid().ToString().Replace("-", ""), typeof(To), new[] { tFrom, typeof(Dictionary<string, POCOMapper>) }, restrictedSkipVisibility: true);
            var il = dynMethod.GetILGenerator();
            var retLoc = il.DeclareLocal(tTo);

            il.Emit(OpCodes.Newobj, cons);                      // [ret]
            il.Emit(OpCodes.Stloc, retLoc);                     // ----

            foreach (var mem in members)
            {
                il.Emit(OpCodes.Ldloc, retLoc);                 // [ret]

                il.Emit(OpCodes.Ldarg_1);                       // [members] [ret]
                il.Emit(OpCodes.Ldstr, mem.Key);                // [memKey] [members] [ret]
                il.Emit(OpCodes.Call, lookup);                  // [Func<object, object>] [ret]

                il.Emit(OpCodes.Ldarg_0);                       // [from] [Func<object, object>] [ret]

                var fromProp = tFrom.GetProperty(mem.Key);
                il.Emit(OpCodes.Callvirt, fromProp.GetMethod);  // [fromVal] [Func<object, object>] [ret]

                if (fromProp.PropertyType.IsValueType)
                {
                    il.Emit(OpCodes.Box, fromProp.PropertyType);// [fromVal] [Func<object, object>] [ret]
                }

                il.Emit(OpCodes.Call, invoke);                  // [toVal (as object)] [ret]

                var toMember = tTo.GetMember(mem.Key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m is FieldInfo || m is PropertyInfo).Single();

                if (toMember is FieldInfo)
                {
                    var asField = (FieldInfo)toMember;

                    if (asField.FieldType.IsValueType)
                    {
                        il.Emit(OpCodes.Unbox_Any, asField.FieldType);  // [toVal] [ret]
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, asField.FieldType);  // [toVal] [ret]
                    }

                    il.Emit(OpCodes.Stfld, asField);                // ----
                }
                else
                {
                    var asProp = (PropertyInfo)toMember;

                    if (asProp.PropertyType.IsValueType)
                    {
                        il.Emit(OpCodes.Unbox_Any, asProp.PropertyType);// [toVal] [ret]
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, asProp.PropertyType);    // [toVal] [ret]
                    }

                    il.Emit(OpCodes.Callvirt, asProp.SetMethod);        // ----
                }
            }

            il.Emit(OpCodes.Ldloc, retLoc);
            il.Emit(OpCodes.Ret);

            var func = (Func<From, Dictionary<string, POCOMapper>, To>)dynMethod.CreateDelegate(typeof(Func<From, Dictionary<string, POCOMapper>, To>));

            return func;
        }

        private static Func<From, Dictionary<string, POCOMapper>, To> BuildRefValueTypeMapper(Dictionary<string, POCOMapper> members)
        {
            var tTo = typeof(To);
            var tFrom = typeof(From);

            var lookup = typeof(POCOMapper<From, To>).GetMethod("Lookup", BindingFlags.Static | BindingFlags.NonPublic);

            var invoke = typeof(Func<object, object>).GetMethod("Invoke");

            var dynMethod = new DynamicMethod("POCOMapper" + Guid.NewGuid().ToString().Replace("-", ""), typeof(To), new[] { tFrom, typeof(Dictionary<string, POCOMapper>) }, restrictedSkipVisibility: true);
            var il = dynMethod.GetILGenerator();
            var retLoc = il.DeclareLocal(tTo);

            il.Emit(OpCodes.Ldloca, retLoc);                    // [*ret]
            il.Emit(OpCodes.Initobj, tTo);                      // ----

            foreach (var mem in members)
            {
                il.Emit(OpCodes.Ldloca, retLoc);                 // [ret]

                il.Emit(OpCodes.Ldarg_1);                       // [members] [ret]
                il.Emit(OpCodes.Ldstr, mem.Key);                // [memKey] [members] [ret]
                il.Emit(OpCodes.Call, lookup);                  // [Func<object, object>] [ret]

                il.Emit(OpCodes.Ldarg_0);                       // [from] [Func<object, object>] [ret]

                var fromProp = tFrom.GetProperty(mem.Key);
                il.Emit(OpCodes.Callvirt, fromProp.GetMethod);  // [fromVal] [Func<object, object>] [ret]

                if (fromProp.PropertyType.IsValueType)
                {
                    il.Emit(OpCodes.Box, fromProp.PropertyType);// [fromVal] [Func<object, object>] [ret]
                }

                il.Emit(OpCodes.Callvirt, invoke);                  // [toVal (as object)] [ret]

                var toMember = tTo.GetMember(mem.Key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m is FieldInfo || m is PropertyInfo).Single();

                if (toMember is FieldInfo)
                {
                    var asField = (FieldInfo)toMember;

                    if (asField.FieldType.IsValueType)
                    {
                        il.Emit(OpCodes.Unbox_Any, asField.FieldType);  // [toVal] [ret]
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, asField.FieldType);  // [toVal] [ret]
                    }

                    il.Emit(OpCodes.Stfld, asField);                // ----
                }
                else
                {
                    var asProp = (PropertyInfo)toMember;

                    if (asProp.PropertyType.IsValueType)
                    {
                        il.Emit(OpCodes.Unbox_Any, asProp.PropertyType);// [toVal] [ret]
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, asProp.PropertyType);    // [toVal] [ret]
                    }

                    il.Emit(OpCodes.Call, asProp.SetMethod);        // ----
                }
            }

            il.Emit(OpCodes.Ldloc, retLoc);
            il.Emit(OpCodes.Ret);

            var func = (Func<From, Dictionary<string, POCOMapper>, To>)dynMethod.CreateDelegate(typeof(Func<From, Dictionary<string, POCOMapper>, To>));

            return func;
        }

        private static POCOMapper GetClassMapper()
        {
            var tFrom = typeof(From);
            var tTo = typeof(To);

            Dictionary<string, POCOMapper> members = null;

            members =
                tFrom
                .GetProperties()
                .ToDictionary(
                    s => s.Name,
                    s =>
                    {
                        var propType = s.PropertyType;

                        var to = tTo.GetMember(s.Name).Where(w => w is FieldInfo || w is PropertyInfo).SingleOrDefault();

                        if (to == null) return null;

                        var toPropType = to is FieldInfo ? (to as FieldInfo).FieldType : (to as PropertyInfo).PropertyType;

                        var mapper = typeof(POCOMapper<,>).MakeGenericType(propType, toPropType);

                        return (POCOMapper)mapper.GetMethod("Get").Invoke(null, new object[0]);
                    }
                ).Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);

            Func<From, Dictionary<string, POCOMapper>, To> func;

            if (tFrom.IsValueType)
            {
                throw new Exception("From should *never* be a value type!");
            }
            else
            {
                if (!tTo.IsValueType)
                {
                    func = BuildRefRefTypeMapper(members);
                }
                else
                {
                    func = BuildRefValueTypeMapper(members);
                }
            }

            Func<object, object> retFunc =
                from =>
                {
                    if (from == null) return null;

                    var asFrom = (From)from;

                    var asTo = func(asFrom, members);

                    return (object)asTo;
                };

            return new POCOMapper(retFunc);
        }

        private static POCOMapper GetAnonymouseClassMapper()
        {
            var tFrom = typeof(From);
            var tTo = typeof(To);

            Dictionary<string, Tuple<FieldInfo, POCOMapper>> members = null;

            var toFields = tTo.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

            members =
                tFrom
                .GetProperties()
                .ToDictionary(
                    s => s.Name,
                    s =>
                    {
                        var propType = s.PropertyType;

                        var to = tTo.GetMember(s.Name).Where(w => w is FieldInfo || w is PropertyInfo).SingleOrDefault();

                        if (to == null) return null;

                        var toPropType = to is FieldInfo ? (to as FieldInfo).FieldType : (to as PropertyInfo).PropertyType;

                        var mapper = typeof(POCOMapper<,>).MakeGenericType(propType, toPropType);

                        var mapperRet = (POCOMapper)mapper.GetMethod("Get").Invoke(null, new object[0]);

                        // OH MY GOD THIS IS A GIANT HACK
                        //  it looks like anonymous type properties always have a backing field that contains <PropName> in their name
                        //  note that that isn't a legal C# name, so collision is basically impossible.
                        //  However, if that behavior changes... oy.
                        var field = toFields.Single(f => f.Name.Contains("<" + s.Name + ">"));

                        return Tuple.Create(field, mapperRet);
                    }
                ).Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);

            var cons = tTo.GetConstructors().Single();

            var consParams = cons.GetParameters();
            var consParamsDefaults = new object[consParams.Length];

            for (var i = 0; i < consParams.Length; i++)
            {
                var type = consParams[i].ParameterType;
                consParamsDefaults[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            var fromType = TypeAccessor.Create(tFrom);

            Func<object, object> retFunc =
                x =>
                {
                    if (x == null) return null;

                    var ret = (To)cons.Invoke(consParamsDefaults);

                    foreach (var mem in members)
                    {
                        var memKey = mem.Key;
                        var memVal = mem.Value;

                        if (memVal == null) continue;

                        var from = fromType[x, memKey];

                        var toField = memVal.Item1;
                        var mapper = memVal.Item2.GetMapper();

                        var fromMapped = mapper(from);

                        toField.SetValue(ret, fromMapped);
                    }

                    return ret;
                };

            return new POCOMapper(retFunc);
        }

        private static POCOMapper GetMapper()
        {
            if (typeof(From) == typeof(To))
            {
                return new POCOMapper(x => (To)x);
            }

            var tFrom = typeof(From);
            var tTo = typeof(To);

            var fIsNullable = Nullable.GetUnderlyingType(tFrom) != null;
            var tIsNullable = Nullable.GetUnderlyingType(tTo) != null;

            if (fIsNullable && !tIsNullable)
            {
                var fNonNull = Nullable.GetUnderlyingType(tFrom);

                var nullMapper = typeof(POCOMapper<,>).MakeGenericType(fNonNull, tTo);
                var nullFunc = (POCOMapper)nullMapper.GetMethod("Get").Invoke(null, new object[0]);

                return
                    new POCOMapper(
                        from =>
                        {
                            var @default = from != null ? from : Activator.CreateInstance(fNonNull);

                            return nullFunc.GetMapper()(@default);
                        }
                    );
            }

            if (!fIsNullable && tIsNullable)
            {
                var tNonNull = Nullable.GetUnderlyingType(tTo);

                var nonNullMapper = typeof(POCOMapper<,>).MakeGenericType(tFrom, tNonNull);
                var nonNullFunc = (POCOMapper)nonNullMapper.GetMethod("Get").Invoke(null, new object[0]);

                return
                    new POCOMapper(
                        from =>
                        {
                            var ret = nonNullFunc.GetMapper()(from);

                            return (To)ret;
                        }
                    );
            }

            if ((tFrom.IsGenericType && tFrom.GetGenericTypeDefinition() == typeof(IDictionary<,>)) ||
               tFrom.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
                if (!((tTo.IsGenericType && tTo.GetGenericTypeDefinition() == typeof(IDictionary<,>)) ||
                    tTo.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))))
                {
                    throw new Exception(tTo.FullName + " is not a valid deserialization target, expected an IDictionary<Key, Value>");
                }

                return GetDictionaryMapper();
            }

            if ((tFrom.IsGenericType && tFrom.GetGenericTypeDefinition() == typeof(IList<>)) ||
               tFrom.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>)))
            {
                if (!((tTo.IsGenericType && tTo.GetGenericTypeDefinition() == typeof(IList<>)) ||
                    tTo.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))))
                {
                    throw new Exception(tTo.FullName + " is not a valid deserialization, expected a list");
                }

                return GetListMapper();
            }

            if (IsAnonymouseClass(tTo))
            {
                return GetAnonymouseClassMapper();
            }

            return GetClassMapper();
        }

        private static bool IsOverride(MethodInfo method)
        {
            return method.GetBaseDefinition() != method;
        }

        /// <summary>
        /// HACK: This is a best effort attempt to divine if a type is anonymous based on the language spec.
        /// 
        /// Reference section 7.6.10.6 of the C# language spec as of 2012/11/19
        /// 
        /// It checks:
        ///     - is a class
        ///     - descends directly from object
        ///     - has [CompilerGenerated]
        ///     - has a single constructor
        ///     - that constructor takes exactly the same parameters as its public properties
        ///     - all public properties are not writable
        ///     - has a private field for every public property
        ///     - overrides Equals(object)
        ///     - overrides GetHashCode()
        /// </summary>
        private static bool IsAnonymouseClass(Type type) // don't fix the typo, it's fitting.
        {
            if (type.IsValueType) return false;
            if (type.BaseType != typeof(object)) return false;
            if (!Attribute.IsDefined(type, typeof(CompilerGeneratedAttribute))) return false;

            var allCons = type.GetConstructors();
            if (allCons.Length != 1) return false;

            var cons = allCons[0];
            if(!cons.IsPublic) return false;

            var props = type.GetProperties();
            if (props.Any(p => p.CanWrite)) return false;

            var propTypes = props.Select(t => t.PropertyType).ToList();

            foreach (var param in cons.GetParameters())
            {
                if (!propTypes.Contains(param.ParameterType)) return false;

                propTypes.Remove(param.ParameterType);
            }

            if (propTypes.Count != 0) return false;

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            if (fields.Any(f => !f.IsPrivate)) return false;

            propTypes = props.Select(t => t.PropertyType).ToList();
            foreach (var field in fields)
            {
                if (!propTypes.Contains(field.FieldType)) return false;

                propTypes.Remove(field.FieldType);
            }

            if (propTypes.Count != 0) return false;

            var equals = type.GetMethod("Equals",new Type[] { typeof(object) });
            var hashCode = type.GetMethod("GetHashCode", new Type[0]);

            if (!IsOverride(equals) || !IsOverride(hashCode)) return false;

            return true;
        }

        public static POCOMapper Get()
        {
            return Mapper ?? PromisedMapper;
        }
    }
}
