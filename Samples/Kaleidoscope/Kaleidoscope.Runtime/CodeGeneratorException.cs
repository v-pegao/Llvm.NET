﻿// -----------------------------------------------------------------------
// <copyright file="CodeGeneratorException.cs" company="Ubiquity.NET Contributors">
// Copyright (c) Ubiquity.NET Contributors. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace Kaleidoscope.Runtime
{
    [Serializable]
    public class CodeGeneratorException
        : Exception
    {
        public CodeGeneratorException( )
        {
        }

        public CodeGeneratorException( string message )
            : base( message )
        {
        }

        public CodeGeneratorException( string message, Exception inner )
            : base( message, inner )
        {
        }

        protected CodeGeneratorException( SerializationInfo info, StreamingContext context )
            : base( info, context )
        {
        }
    }
}
