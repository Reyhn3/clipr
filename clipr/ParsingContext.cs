﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using clipr.Arguments;
using clipr.Triggers;

namespace clipr
{
    internal class ParsingContext<T> where T : class
    {
        private ParserConfig<T> Config { get; set; }

        private T Object { get; set; }

        private HashSet<string> ParsedMutuallyExclusiveGroups { get; set; }

        public ParsingContext(T obj, ParserConfig<T> config)
        {
            Object = obj;
            Config = config;

            ParsedMutuallyExclusiveGroups = new HashSet<string>();
        }

        /// <summary>
        /// Parse the given argument list.
        /// </summary>
        /// <exception cref="ParseException">
        /// An error happened while parsing.
        /// </exception>
        /// <exception cref="ParserExit">
        /// Either the help or version information were triggered so
        /// parsing was aborted.
        /// </exception>
        /// <param name="args">Argument list to parse.</param>
        public void Parse(string[] args)
        {
            var positionalDelimiter = "" + Config.ArgumentPrefix + Config.ArgumentPrefix;

            var values = new Stack<string>(args.Reverse());

            var positionalArgumentStore = new List<string>();

            while (values.Count > 0)
            {
                var arg = values.Pop();

                // Empty arg
                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                arg = arg.Trim();

                // Rest of arguments are positional
                if (arg == positionalDelimiter)
                {
                    positionalArgumentStore.AddRange(values);
                    break;
                }

                if (arg[0] == Config.ArgumentPrefix)
                {
                    if (arg.Length == 1)  // myprog.exe -
                    {
                        throw new ParseException(arg, String.Format(
                            @"Cannot use argument prefix ""{0}"" as " +
                            @"argument unless forced into positional " +
                            @"mode using ""{1}"".",
                            Config.ArgumentPrefix, positionalDelimiter));
                    }
                    if (arg[1] == Config.ArgumentPrefix)  // myprog.exe --arg
                    {
                        var partition = arg.Substring(2).Split(Config.LongOptionSeparator, 2);
                        if (partition.Length > 1)
                        {
                            values.Push(partition[1]);
                        }
                        ParseOptionalArgument(partition[0], Config.LongNameArguments, values);
                    }
                    else  // myprog.exe -a
                    {
                        var shortArg = arg[1];

                        if (Char.IsDigit(shortArg))  // myprog.exe -1
                        {
                            // We're parsing a negative number...
                            // Nothing to see here.
                            positionalArgumentStore.Add(arg);
                            continue;
                        }

                        if (arg.Length > 2)
                        {
                            // Pretend the rest of the argument is a new value
                            values.Push(arg.Substring(2));

                            var stackSize = values.Count;
                            ParseOptionalArgument(shortArg, Config.ShortNameArguments, values);

                            // No arguments were used... the arg
                            // we just pushed must be a group of
                            // short args.
                            if (values.Count == stackSize)
                            {
                                foreach (var shortName in values.Pop())
                                {
                                    // Arguments in a group cannot consume
                                    // values from the argStack
                                    ParseOptionalArgument(shortName, Config.ShortNameArguments, values);
                                }
                            }
                        }
                        else  // Just a single argument
                        {
                            ParseOptionalArgument(shortArg, Config.ShortNameArguments, values);
                        }
                    }
                    continue;
                }

                positionalArgumentStore.Add(arg);
            }

            positionalArgumentStore.Reverse();
            var positionalArgStack = new Stack<string>(positionalArgumentStore);
            ParsePositionalArguments(positionalArgStack);

            if (positionalArgStack.Count > 0)
            {
                throw new ParseException(null, String.Format(
                    "Extra positional arguments found: {0}",
                    String.Join(" ", positionalArgStack.ToArray())));
            }

            ParsingCleanup();
        }

        #region Private Parsing Methods

        private void ParseOptionalArgument<TS>(TS name, Dictionary<TS, IShortNameArgument> argDict, Stack<string> iter)
        {
            var newDict = argDict.ToDictionary(
                k => k.Key,
                v => v.Value as IArgument,
                argDict.Comparer);
            ParseOptionalArgument(name, newDict, iter);
        }

        private void ParseOptionalArgument<TS>(TS name, Dictionary<TS, ILongNameArgument> argDict, Stack<string> iter)
        {
            var newDict = argDict.ToDictionary(
                k => k.Key,
                v => v.Value as IArgument,
                argDict.Comparer);
            ParseOptionalArgument(name, newDict, iter);
        }

        private void ParseOptionalArgument<TS>(TS name, IDictionary<TS, IArgument> argDict, Stack<string> iter)
        {
            IArgument arg;
            if (!argDict.TryGetValue(name, out arg))
            {
                throw new ParseException(name.ToString(), String.Format(
                    "Unknown argument name '{0}'.", name));
            }

            if (arg is ITrigger<T>)
            {
                (arg as ITrigger<T>).OnParse();
                throw new ParserExit();
            }

            if (arg.MutuallyExclusiveGroups != null)
            {
                foreach (var group in arg.MutuallyExclusiveGroups)
                {
                    if (!ParsedMutuallyExclusiveGroups.Add(group))
                    {
                        throw new ParseException(name.ToString(), String.Format(
                            @"Mutually exclusive group ""{0}"" violated.",
                            group));
                    }
                }
            }

            if (iter == null && arg.Action.ConsumesArgumentValues())
            {
                throw new ParseException(name.ToString(),
                    "Arguments that consume values cannot be grouped.");
            }
            ParseArgument(name.ToString(), arg, iter);
        }

        private void ParsePositionalArguments(Stack<string> args)
        {
            foreach (var arg in Config.PositionalArguments)
            {
                ParseArgument(arg.Name.ToLowerInvariant(), arg, args);
            }
        }

        /// <summary>
        /// Common parsing code, regardless of argument type.
        /// </summary>
        /// <param name="attrName">
        /// Name of the argument (whether short, long, or positional).
        /// </param>
        /// <param name="arg">Property associated with the argument.</param>
        /// <param name="args">List of remaining unparsed arguments.</param>
        private void ParseArgument(string attrName, IArgument arg, Stack<string> args)
        {
            var prop = arg.Property;
            switch (arg.Action)
            {
                case ParseAction.Store:
                    {
                        if (!arg.ConsumesMultipleArgs)
                        {
                            if (args.Count == 0)
                            {
                                throw new ParseException(attrName, String.Format(
                                    @"Argument ""{0}"" requires a value but " +
                                    "none was provided.", attrName));
                            }
                            var stringValue = args.Pop();
                            try
                            {
                                prop.SetValue(Object, ConvertFrom(prop, stringValue), null);
                            }
                            catch (Exception e)
                            {
                                throw new ParseException(attrName, String.Format(
                                    @"Value ""{0}"" cannot be converted to the " +
                                    "required type {1}.",
                                    stringValue, prop.PropertyType), e);
                            }
                        }
                        else
                        {
                            var existing = (IEnumerable)prop.GetValue(Object, null);
                            var backingList = CreateGenericList(prop, existing);

                            ParseVarargs(attrName, backingList, arg, args);
                            prop.SetValue(Object, backingList, null);
                        }
                        break;
                    }
                case ParseAction.StoreConst:
                    prop.SetValue(Object, arg.Const, null);
                    break;

                case ParseAction.StoreTrue:
                    prop.SetValue(Object, true, null);
                    break;

                case ParseAction.StoreFalse:
                    prop.SetValue(Object, false, null);
                    break;

                case ParseAction.Append:
                    {
                        var existing = (IEnumerable)prop.GetValue(Object, null);
                        var backingList = CreateGenericList(prop, existing);

                        if (!arg.ConsumesMultipleArgs)
                        {
                            if (args.Count == 0)
                            {
                                throw new ParseException(attrName);
                            }
                            var stringValue = args.Pop();
                            try
                            {
                                backingList.Add(ConvertFromGeneric(prop, stringValue));
                            }
                            catch (Exception e)
                            {
                                throw new ParseException(attrName, String.Format(
                                    @"Value ""{0}"" cannot be converted to the " +
                                    "required type {1}.",
                                    stringValue, prop.PropertyType), e);
                            }
                        }
                        else
                        {
                            ParseVarargs(attrName, backingList, arg, args);

                        }
                        prop.SetValue(Object, backingList, null);
                        break;
                    }
                case ParseAction.AppendConst:
                    {
                        var existing = (IEnumerable) prop.GetValue(Object, null);
                        var backingList = CreateGenericList(prop, existing);
                        backingList.Add(arg.Const);
                        prop.SetValue(Object, backingList, null);
                        break;
                    }
                case ParseAction.Count:
                    var cnt = (int)prop.GetValue(Object, null);
                    prop.SetValue(Object, cnt + 1, null);
                    break;
            }
        }

        private void ParseVarargs(string attrName, IList list,
            IArgument arg, Stack<string> args)
        {
            var argsProcessed = 0;

            #region Set minimum and maximum argument count.

            uint minArgs = 0;
            uint maxArgs = 0;
            switch (arg.Constraint)
            {
                case NumArgsConstraint.Exactly:
                    minArgs = arg.NumArgs;
                    maxArgs = arg.NumArgs;
                    break;
                case NumArgsConstraint.AtLeast:
                    minArgs = arg.NumArgs;
                    maxArgs = uint.MaxValue;
                    break;
                case NumArgsConstraint.AtMost:
                    minArgs = 0;
                    maxArgs = arg.NumArgs;
                    break;
            }

            #endregion

            while (args.Count > 0 && argsProcessed < maxArgs)
            {
                var stringValue = args.Pop();

                // Quit if we start a new argument here.
                if (stringValue != null &&
                    stringValue.StartsWith(Config.ArgumentPrefix.ToString()))
                {
                    args.Push(stringValue);
                    break;
                }
                try
                {
                    list.Add(ConvertFromGeneric(arg.Property, stringValue));
                }
                catch (Exception e)
                {
                    throw new ParseException(attrName, String.Format(
                        @"Value ""{0}"" cannot be converted to the " +
                        "required type {1}.",
                        stringValue, arg.Property.PropertyType), e);
                }
                argsProcessed++;
            }

            if (argsProcessed < minArgs)
            {
                throw new ParseException(attrName, String.Format(
                    "Parameter {0} requires {1} {2} value(s).",
                    arg.MetaVar ?? arg.Name,
                    arg.Constraint == NumArgsConstraint.Exactly ?
                        "exactly" : "at least",
                    minArgs));
            }
        }

        #endregion

        private void ParsingCleanup()
        {
            var missingRequiredMutexGroups = Config
                .RequiredMutuallyExclusiveArguments
                .Except(ParsedMutuallyExclusiveGroups);
            if (missingRequiredMutexGroups.Any())
            {
                throw new ParseException(null, String.Format(
                    @"Required mutually exclusive group(s) ""{0}"" were " +
                    "not provided.",
                    String.Join(", ", missingRequiredMutexGroups.ToArray())));
            }

            foreach (var method in Config.PostParseMethods)
            {
                method.Invoke(Object, null);
            }
        }

        private static object ConvertFrom(PropertyInfo prop, string value)
        {
            return TypeDescriptor.GetConverter(prop.PropertyType)
                                    .ConvertFromInvariantString(value);
        }

        private static object ConvertFromGeneric(PropertyInfo prop, string value)
        {
            return TypeDescriptor.GetConverter(
                prop.PropertyType.GetGenericArguments().First())
                .ConvertFromInvariantString(value);
        }

        private static IList CreateGenericList(PropertyInfo prop, IEnumerable initial)
        {
            var type = prop.PropertyType.GetGenericArguments();
            if (!type.Any())
            {
                return null;
            }
            var list = (IList) Activator.CreateInstance(typeof (List<>).MakeGenericType(type));
            if (initial != null)
            {
                foreach (var elem in initial)
                {
                    list.Add(elem);
                }
            }
            return list;
        }
    }
}