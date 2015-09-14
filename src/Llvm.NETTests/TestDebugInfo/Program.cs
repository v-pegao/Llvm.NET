﻿using System;
using System.Collections.Generic;
using System.IO;
using Llvm.NET;
using Llvm.NET.DebugInfo;
using Llvm.NET.Instructions;
using Llvm.NET.Types;
using Llvm.NET.Values;

namespace TestDebugInfo
{
    /// <summary>Program to test/demonstrate Aspects of debug information generation with Llvm.NET</summary>
    class Program
    {
        const string Triple = "x86_64-pc-windows-msvc18.0.0";
        const string Cpu = "x86-64";
        const string Features = "+sse,+sse2";
        static readonly Dictionary<string, string> TargetDependentAttributes = new Dictionary<string, string>
        {
            [ "disable-tail-calls" ] = "false",
            [ "less-precise-fpmad" ] = "false",
            [ "no-frame-pointer-elim" ] = "false",
            [ "no-infs-fp-math" ] = "false",
            [ "no-nans-fp-math" ] = "false",
            [ "stack-protector-buffer-size" ] = "8",
            [ "target-cpu" ] = "x86-64",
            [ "target-features" ] = "+sse,+sse2",
            [ "unsafe-fp-math" ] = "false",
            [ "use-soft-float" ] = "false",
        };

        /// <summary>Creates a test LLVM module with debug information</summary>
        /// <param name="args">ignored</param>
        /// <remarks>
        /// <code language="C++" title="Example code generated">
        /// struct foo
        /// {
        ///     int a;
        ///     float b;
        ///     int c[2];
        /// };
        /// 
        /// struct foo bar = { 1, 2.0, { 3, 4 } };
        /// struct foo baz;
        /// 
        /// inline static void copy( struct foo src     // function line here
        ///                        , struct foo* pDst
        ///                        )
        /// { // function's ScopeLine here
        ///     *pDst = src;
        /// }
        /// 
        /// void DoCopy( )
        /// {
        ///     copy( bar, &baz );
        /// }
        /// </code>
        /// </remarks>
        static void Main( string[ ] args )
        {
            var srcPath = args[0];
            if( !File.Exists( srcPath ) )
            {
                Console.Error.WriteLine( "Src file not found: '{0}'", srcPath );
                return;
            }

            StaticState.RegisterAll( );
            var target = Target.FromTriple( Triple );
            using( var targetMachine = target.CreateTargetMachine( Triple, Cpu, Features, CodeGenOpt.Aggressive, Reloc.Default, CodeModel.Small ) )
            using( var module = new Module( "test_x86.bc" ) )
            {
                var targetData = targetMachine.TargetData;

                module.TargetTriple = targetMachine.Triple;
                module.DataLayoutString = targetMachine.TargetData.ToString( );

                // create compile unit and file as the top level scope for everything
                var cu = module.DIBuilder.CreateCompileUnit( SourceLanguage.C99
                                                            , Path.GetFileName( srcPath )
                                                            , Path.GetDirectoryName( srcPath )
                                                            , "clang version 3.7.0 " // obviously not clang but helps in diff with actual clang output
                                                            , false
                                                            , ""
                                                            , 0
                                                            );
                var diFile = module.DIBuilder.CreateFile( srcPath );

                // Create basic types used in this compilation
                module.Context.Int32Type.CreateDIType( module, "int", cu );
                module.Context.FloatType.CreateDIType( module, "float", cu );
                var i32 = module.Context.Int32Type;
                var f32 = module.Context.FloatType;
                var i32Array_0_2 = i32.CreateArrayType( module, 0, 2 );

                // create the LLVM structure type and body
                // The full body is used with targetMachine.TargetData to determine size, alignment and element offsets
                // in a target independent manner.
                var fooType = module.Context.CreateStructType( "struct.foo" );
                fooType.CreateDIType( module, "foo", cu );
                fooType.SetBody( false, module.Context.Int32Type, module.Context.FloatType, i32Array_0_2 );

                // add global variables and constants
                var constArray = ConstantArray.From( i32, module.Context.CreateConstant( 3 ), module.Context.CreateConstant( 4 ));
                var barValue = module.Context.CreateNamedConstantStruct( fooType
                                                                       , module.Context.CreateConstant( 1 )
                                                                       , module.Context.CreateConstant( 2.0f )
                                                                       , constArray
                                                                       );

                var bar = module.AddGlobal( fooType, false, 0, barValue, "bar" );
                bar.Alignment = targetData.AbiAlignmentOf( fooType );
                module.DIBuilder.CreateGlobalVariable( cu, "bar", string.Empty, diFile, 8, fooType.DIType, false, bar );

                var baz = module.AddGlobal( fooType, false, Linkage.Common, Constant.NullValueFor( fooType ), "baz" );
                baz.Alignment = targetData.AbiAlignmentOf( fooType );
                module.DIBuilder.CreateGlobalVariable( cu, "baz", string.Empty, diFile, 9, fooType.DIType, false, baz );

                // add module flags and ident
                // this can technically occur at any point, though placing it here makes
                // comparing against clang generated files simpler
                module.AddModuleFlag( ModuleFlagBehavior.Warning, Module.DwarfVersionValue, 4 );
                module.AddModuleFlag( ModuleFlagBehavior.Warning, Module.DebugVersionValue, Module.DebugMetadataVersion );
                module.AddModuleFlag( ModuleFlagBehavior.Error, "PIC Level", 2 );
                module.AddVersionIdentMetadata( "clang version 3.7.0 " );

                // create types for function args
                var constFoo = module.DIBuilder.CreateQualifiedType( fooType.DIType, QualifiedTypeTag.Const );
                var fooPtr = fooType.CreatePointerType( module );
                //var constFooPtr = module.DIBuilder.CreatePointerType( constFoo, string.Empty, targetData.BitSizeOf( fooPtr ), targetData.AbiBitAlignmentOf( fooPtr ) );
                // Create function signatures

                // Since the the first parameter is passed by value 
                // using the pointer+alloca+memcopy pattern, the actual
                // source, and therefore debug, signature is NOT a pointer.
                // However, that usage would create a signature with two
                // pointers as the arguments, which doesn't match the source
                // To get the correct debug info signature this inserts an
                // explicit ParameterTypePair that overrides the default
                // behavior to pair LLVM pointer type with the original
                // source type.
                var copySig = module.Context.CreateFunctionType( module.DIBuilder
                                                               , diFile
                                                               , module.Context.VoidType
                                                               , new ParameterTypePair( fooPtr, constFoo )
                                                               , fooPtr
                                                               );
                var doCopySig = module.Context.CreateFunctionType( module.DIBuilder, diFile, module.Context.VoidType );

                // Create the functions
                // NOTE: The declaration ordering is reveresd from that of the sample code file (test.c)
                //       However, this is what Clang ends up doing for some reason so it is
                //       replicated here to aid in comparing the generated LL files.
                var doCopyFunc = module.CreateFunction( scope: diFile
                                                      , name: "DoCopy"
                                                      , linkageName: null
                                                      , file: diFile
                                                      , line: 23
                                                      , signature: doCopySig
                                                      , isLocalToUnit: false
                                                      , isDefinition: true
                                                      , scopeLine: 24
                                                      , flags: 0
                                                      , isOptimized: false
                                                      ).AddAttributes( Attributes.NoUnwind | Attributes.UnwindTable )
                                                      .AddAttributes( TargetDependentAttributes );

                var copyFunc = module.CreateFunction( scope: diFile
                                                    , name: "copy"
                                                    , linkageName: null
                                                    , file: diFile
                                                    , line: 11
                                                    , signature: copySig
                                                    , isLocalToUnit: true
                                                    , isDefinition: true
                                                    , scopeLine: 14
                                                    , flags: DebugInfoFlags.Prototyped
                                                    , isOptimized: false
                                                    ).AddAttributes( Attributes.NoUnwind | Attributes.UnwindTable | Attributes.InlineHint )
                                                     .AddAttributes( TargetDependentAttributes )
                                                     .Linkage( Linkage.Internal ); // static function

                CreateDoCopyFunctionBody( module, targetData, doCopyFunc, fooType, bar, baz, copyFunc );
                CreateCopyFunctionBody( module, targetData, copyFunc, diFile, fooType, fooPtr, constFoo );

                // fill in the debug info body for type foo
                finalizeFooDebugInfo( module.DIBuilder, targetData, cu, diFile, i32, i32Array_0_2, f32, fooType );

                // finalize the debug information
                // all temporaries must be replaced by now, this resolves any remaining
                // forward declarations and marks the builder to prevent adding any
                // nodes that are not completely resolved.
                module.DIBuilder.Finish( );

                // verify the module is still good and print any errors found
                string msg;
                if( !module.Verify( out msg ) )
                {
                    Console.Error.WriteLine( "ERROR: {0}", msg );
                }
                else
                {
                    // Module is good, so generate the output files
                    module.WriteToFile( "test.bc" );
                    File.WriteAllText( "test.ll", module.AsString( ) );
                    targetMachine.EmitToFile( module, "test.o", CodeGenFileType.ObjectFile );
                    targetMachine.EmitToFile( module, "test.s", CodeGenFileType.AssemblySource );
                }
            }
        }

        private static void finalizeFooDebugInfo( DebugInfoBuilder diBuilder
                                                , TargetData layout
                                                , DICompileUnit cu
                                                , DIFile diFile
                                                , TypeRef i32
                                                , TypeRef i32Array_0_2
                                                , TypeRef f32
                                                , StructType foo
                                                )
        {
            // Create concrete DIType and RAUW of the opaque one with the complete version
            // While this two phase approach isn't strictly necessary in this sample it
            // isn't an uncommon case in the real world so this example demonstrates how
            // to use forward type decalarations and replace them with a complete type when
            // all of the type information is available
            var diFields = new DIType[ ]
                { diBuilder.CreateMemberType( scope: foo.DIType
                                            , name: "a"
                                            , file: diFile
                                            , line: 3
                                            , bitSize: layout.BitSizeOf( i32 )
                                            , bitAlign: layout.AbiBitAlignmentOf( i32 )
                                            , bitOffset: layout.BitOffsetOfElement( foo, 0 )
                                            , flags: 0
                                            , type: i32.DIType
                                            )
                , diBuilder.CreateMemberType( scope: foo.DIType
                                            , name: "b"
                                            , file: diFile
                                            , line: 4
                                            , bitSize: layout.BitSizeOf( f32 )
                                            , bitAlign: layout.AbiBitAlignmentOf( f32 )
                                            , bitOffset: layout.BitOffsetOfElement( foo, 1 )
                                            , flags: 0
                                            , type: f32.DIType
                                            )
                , diBuilder.CreateMemberType( scope: foo.DIType
                                            , name: "c"
                                            , file: diFile
                                            , line: 5
                                            , bitSize: layout.BitSizeOf( i32Array_0_2 )
                                            , bitAlign: layout.AbiBitAlignmentOf( i32Array_0_2 )
                                            , bitOffset: layout.BitOffsetOfElement( foo, 2 )
                                            , flags: 0
                                            , type: i32Array_0_2.DIType
                                            )
                };
            var fooConcrete = diBuilder.CreateStructType( scope: cu
                                                        , name: "foo"
                                                        , file: diFile
                                                        , line: 1
                                                        , bitSize: layout.BitSizeOf( foo )
                                                        , bitAlign: layout.AbiBitAlignmentOf( foo )
                                                        , flags: 0
                                                        , derivedFrom: null
                                                        , elements: diFields );
            foo.ReplaceAllUsesOfDebugTypeWith( fooConcrete );
        }

        private static void CreateCopyFunctionBody( Module module
                                                  , TargetData layout
                                                  , Function copyFunc
                                                  , DIFile diFile
                                                  , StructType foo
                                                  , PointerType fooPtr
                                                  , DIDerivedType constFooType
                                                  )
        {
            var diBuilder = module.DIBuilder;

            // ByVal pointers indicate by value semantics. LLVM recognizes this pattern and has a pass to map
            // to an efficient register usage whenever plausible.
            // Though it seems unnecessary as Clang doesn't apply the attribute...
            //copyFunc.Parameters[ 0 ].AddAttributes( Attributes.ByVal );
            //copyFunc.Parameters[ 0 ].SetAlignment( foo.AbiAlignment );
            copyFunc.Parameters[ 0 ].Name = "src";
            copyFunc.Parameters[ 1 ].Name = "pDst";

            // create block for the function body, only need one for this simple sample
            var blk = copyFunc.AppendBasicBlock( "entry" );

            // create instruction builder to build the body
            var instBuilder = new InstructionBuilder( blk );

            // create debug info locals for the arguments
            // NOTE: Debug parameter indeces are 1 based!
            var paramSrc = diBuilder.CreateArgument( copyFunc.DISubProgram, "src", diFile, 11, constFooType, false, 0, 1 );
            var paramDst = diBuilder.CreateArgument( copyFunc.DISubProgram, "pDst", diFile, 12, fooPtr.DIType, false, 0, 2 );

            var ptrAlign = layout.CallFrameAlignmentOf( fooPtr );
            
            // create Locals
            // NOTE: There's no debug location attatched to these instructions.
            //       The debug info will come from the declare instrinsic below.
            var dstAddr = instBuilder.Alloca( fooPtr, "pDst.addr" )
                                     .Alignment( ptrAlign );

            var dstStore = instBuilder.Store( copyFunc.Parameters[ 1 ], dstAddr)
                                      .Alignment( ptrAlign );

            // insert declare pseudo instruction to attach debug info to the local declarations
            var dstDeclare = diBuilder.InsertDeclare( dstAddr, paramDst, new DILocation( module.Context, 12, 38, copyFunc.DISubProgram ), blk );

            // since the function's LLVM signature uses a pointer, which is copied locally
            // inform the debugger to treat it as the value by dereferencing the pointer
            var srcDeclare = diBuilder.InsertDeclare( copyFunc.Parameters[ 0 ]
                                                           , paramSrc
                                                           , diBuilder.CreateExpression( ExpressionOp.deref )
                                                           , new DILocation( module.Context, 11, 43, copyFunc.DISubProgram )
                                                           , blk
                                                           );

            var loadedDst = instBuilder.Load( dstAddr )
                                       .Alignment( ptrAlign )
                                       .SetDebugLocation( 15, 6, copyFunc.DISubProgram );

            var dstPtr = instBuilder.BitCast( loadedDst, module.Context.Int8Type.CreatePointerType( ) )
                                    .SetDebugLocation( 15, 13, copyFunc.DISubProgram );

            var srcPtr = instBuilder.BitCast( copyFunc.Parameters[ 0 ], module.Context.Int8Type.CreatePointerType( ) )
                                    .SetDebugLocation( 15, 13, copyFunc.DISubProgram );

            var memCpy = instBuilder.MemCpy( module
                                           , dstPtr
                                           , srcPtr
                                           , module.Context.CreateConstant( layout.ByteSizeOf( foo ) )
                                           , ( int )layout.AbiAlignmentOf( foo )
                                           , false
                                           ).SetDebugLocation( 15, 13, copyFunc.DISubProgram );

            var ret = instBuilder.Return( )
                                 .SetDebugLocation( 16, 1, copyFunc.DISubProgram );
        }

        private static void CreateDoCopyFunctionBody( Module module
                                                    , TargetData layout
                                                    , Function doCopyFunc
                                                    , StructType foo
                                                    , GlobalVariable bar
                                                    , GlobalVariable baz
                                                    , Function copyFunc
                                                    )
        {
            var diBuilder = module.DIBuilder;

            var bytePtrType = module.Context.Int8Type.CreatePointerType( );

            // create block for the function body, only need one for this simple sample
            var blk = doCopyFunc.AppendBasicBlock( "entry" );

            // create instruction builder to build the body
            var instBuilder = new InstructionBuilder( blk );

            // create a temp local copy of the global structure
            var dstAddr = instBuilder.Alloca( foo, "agg.tmp" )
                                     .Alignment( layout.CallFrameAlignmentOf( foo ) );

            var bitCastDst = instBuilder.BitCast( dstAddr, bytePtrType )
                                        .SetDebugLocation( 25, 11, doCopyFunc.DISubProgram );

            var bitCastSrc = instBuilder.BitCast( bar, bytePtrType )
                                        .SetDebugLocation( 25, 11, doCopyFunc.DISubProgram );

            var memCpy = instBuilder.MemCpy( module
                                           , bitCastDst
                                           , bitCastSrc
                                           , module.Context.CreateConstant( layout.ByteSizeOf( foo ) )
                                           , ( int )layout.CallFrameAlignmentOf( foo )
                                           , false
                                           ).SetDebugLocation( 25, 11, doCopyFunc.DISubProgram );

            var callCopy = instBuilder.Call( copyFunc, dstAddr, baz )
                                      .SetDebugLocation( 25, 5, doCopyFunc.DISubProgram );

            var ret =instBuilder.Return( )
                                .SetDebugLocation( 26, 1, doCopyFunc.DISubProgram );
        }
    }
}
