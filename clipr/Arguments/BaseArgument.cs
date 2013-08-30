﻿using System.Collections.Generic;
using System.Reflection;

namespace clipr.Arguments
{
    /// <summary>
    /// Defines the basic properties of an argument.
    /// </summary>
    internal abstract class BaseArgument : IArgument
    {
        public string Name { get; internal set; }

        public string Description { get; set; }

        public List<string> MutuallyExclusiveGroups { get; set; }

        public uint NumArgs { get; set; }

        public NumArgsConstraint Constraint { get; set; }

        public string MetaVar { get; set; }

        public object Const { get; set; }
        
        public ParseAction Action { get; set; }

        public bool ConsumesMultipleArgs
        {
            get
            {
                return NumArgs != 0 && 
                    (Constraint != NumArgsConstraint.Exactly ||
                    NumArgs > 1);
            }
        }

        public PropertyInfo Property { get; set; }

        /// <summary>
        /// Create a new Argument.
        /// </summary>
        protected BaseArgument(PropertyInfo prop)
        {
            Initialize(prop);
        }

        private void Initialize(PropertyInfo prop)
        {
            Name = prop.Name;
            MetaVar = Name;
            Property = prop;
            NumArgs = 1;
        }
    }
}