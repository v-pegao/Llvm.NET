﻿// <copyright file="DIScope.cs" company=".NET Foundation">
// Copyright (c) .NET Foundation. All rights reserved.
// </copyright>

using Llvm.NET.Native;

namespace Llvm.NET.DebugInfo
{
    /// <summary>Base class for all Debug info scopes</summary>
    public abstract class DIScope
        : DINode
    {
        /// <summary>Gets the <see cref="DIFile"/> describing the file this scope belongs to</summary>
        public DIFile File
        {
            get
            {
                var handle = NativeMethods.LLVMDIScopeGetFile( MetadataHandle );
                if( handle == default )
                {
                    return null;
                }

                return FromHandle<DIFile>( handle );
            }
        }

        /// <summary>Gets the parent scope for this scope or <see langword="null"/> if no parent</summary>
        public virtual DIScope Scope { get; } = null;

        /// <summary>Gets the name of the scope or an empty string if the scope doesn't have a name</summary>
        public virtual string Name { get; } = string.Empty;

        internal DIScope( LLVMMetadataRef handle )
            : base( handle )
        {
        }
    }
}
