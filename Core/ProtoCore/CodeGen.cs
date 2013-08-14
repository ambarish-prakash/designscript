﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ProtoCore.AST;
using ProtoCore.DSASM;
using ProtoCore.Utils;

namespace ProtoCore
{
    public class BackpatchNode
    {
        public int bp;
        public int pc;
    }

    public class BackpatchTable
    {
        public List<BackpatchNode> backpatchList { get; protected set; }
        public BackpatchTable()
        {
            backpatchList = new List<BackpatchNode>();
        }

        public void Append(int bp, int pc)
        {
            BackpatchNode node = new BackpatchNode();
            node.bp = bp;
            node.pc = pc;
            backpatchList.Add(node);
        }

        public void Append(int bp)
        {
            BackpatchNode node = new BackpatchNode();
            node.bp = bp;
            node.pc = ProtoCore.DSASM.Constants.kInvalidIndex;
            backpatchList.Add(node);
        }
    }

    public abstract class CodeGen
    {
        protected Core core;
        protected bool emitReplicationGuide;

        //protected static Dictionary<ulong, ulong> codeToLocation = new Dictionary<ulong, ulong>();
        //public static Dictionary<ulong, ulong> codeToLocation = new Dictionary<ulong, ulong>();
    
        protected int pc;
        protected int argOffset;
        protected int classOffset;
        protected int globalClassIndex;
        protected bool dumpByteCode;
        protected bool isAssocOperator;
        protected bool isEntrySet;
        protected int targetLangBlock;
        protected int blockScope;
        protected bool enforceTypeCheck;
        public ProtoCore.DSASM.CodeBlock codeBlock { get; set; }
        public ProtoCore.CompileTime.Context context { get; set; }
        protected ProtoCore.DSASM.OpKeywordData opKwData;
        protected BuildStatus buildStatus;

        protected int globalProcIndex;
        protected ProtoCore.DSASM.ProcedureNode localProcedure;
        protected ProtoCore.AST.Node localFunctionDefNode;
        protected ProtoCore.AST.Node localCodeBlockNode;
        protected bool emitDebugInfo = true;
        protected int tryLevel;

        protected int currentBinaryExprUID = ProtoCore.DSASM.Constants.kInvalidIndex;
        protected List<ProtoCore.DSASM.ProcedureNode> functionCallStack;
        protected bool IsAssociativeArrayIndexing { get; set; }

        protected bool isEmittingImportNode = false;

        // The parser currently inteprets floating point literal values as being 
        // separated by a period character '.', in cultural settings like German,
        // this will not be the case (they use ',' as decimal separation character).
        // For this reason, whenever "Convert.ToDouble" method is used to convert 
        // a 'string' value into a 'double' value, the conversion cannot be based 
        // on the current system culture (e.g. "de-DE"), it needs to be able to 
        // parse the string in "en-US" format (because that's how the parser is 
        // made to recognize floating point numbers.
        // 
        protected CultureInfo cultureInfo = new CultureInfo("en-US");


        // Contains the list of Nodes in an identifier list
        protected List<ProtoCore.AST.AssociativeAST.AssociativeNode> ssaPointerList;

        // The first graphnode of the SSA'd identifier
        protected ProtoCore.AssociativeGraph.GraphNode firstSSAGraphNode = null;

        // These variables hold data when backtracking static SSA'd calls
        protected string staticClass = null;
        protected bool resolveStatic = false;

        public CodeGen(Core coreObj, ProtoCore.DSASM.CodeBlock parentBlock = null)
        {
            Debug.Assert(coreObj != null);
            core = coreObj;
            buildStatus = core.BuildStatus;
            isEntrySet = false;

            emitReplicationGuide = false;

            dumpByteCode = core.Options.DumpByteCode;
            isAssocOperator = false;

            pc = 0;
            argOffset = 0;
            globalClassIndex = core.ClassIndex;

            context = new ProtoCore.CompileTime.Context();
            opKwData = new ProtoCore.DSASM.OpKeywordData();

            targetLangBlock = ProtoCore.DSASM.Constants.kInvalidIndex;

            enforceTypeCheck = true;

            localProcedure = core.ProcNode;
            globalProcIndex = null != localProcedure ? localProcedure.procId : ProtoCore.DSASM.Constants.kGlobalScope;

            tryLevel = 0;

            functionCallStack = new List<DSASM.ProcedureNode>();

            IsAssociativeArrayIndexing = false;

            if (core.AsmOutput == null)
            {
                if (core.Options.CompileToLib)
                {
                    string path = "";
                    if (core.Options.LibPath == null)
                    {
                        path += core.Options.RootModulePathName + "ASM";
                    }
                    else
                    {
                        path = Path.Combine(core.Options.LibPath, Path.GetFileNameWithoutExtension(core.Options.RootModulePathName) + ".dsASM");
                    }

                    core.AsmOutput = new StreamWriter(File.Open(path, FileMode.Create));
                }
                else
                {
                    core.AsmOutput = Console.Out;
                }
            }

            ssaPointerList = new List<AST.AssociativeAST.AssociativeNode>();
        }

        protected ProtoCore.DSASM.AddressType GetOpType(ProtoCore.PrimitiveType type)
        {
            ProtoCore.DSASM.AddressType optype = ProtoCore.DSASM.AddressType.Int;
            // Data coercion for the prototype
            // The JIL executive handles int primitives
            if (ProtoCore.PrimitiveType.kTypeInt == type
                || ProtoCore.PrimitiveType.kTypeBool == type
                || ProtoCore.PrimitiveType.kTypeChar == type
                || ProtoCore.PrimitiveType.kTypeString == type)
            {
                optype = ProtoCore.DSASM.AddressType.Int;
            }
            else if (ProtoCore.PrimitiveType.kTypeDouble == type)
            {
                optype = ProtoCore.DSASM.AddressType.Double;
            }
            else if (ProtoCore.PrimitiveType.kTypeVar == type)
            {
                optype = ProtoCore.DSASM.AddressType.VarIndex;
            }
            else if (ProtoCore.PrimitiveType.kTypeReturn == type)
            {
                optype = ProtoCore.DSASM.AddressType.Register;
            }
            else
            {
                Debug.Assert(false);
            }
            return optype;
        }

        protected ProtoCore.DSASM.StackValue BuildOperand(ProtoCore.DSASM.SymbolNode symbol)
        {
            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();

            if (symbol.classScope != ProtoCore.DSASM.Constants.kInvalidIndex &&
                symbol.functionIndex == ProtoCore.DSASM.Constants.kGlobalScope)
            {
                // Member var
                op.optype = (symbol.isStatic) ? ProtoCore.DSASM.AddressType.StaticMemVarIndex : ProtoCore.DSASM.AddressType.MemVarIndex;
                op.opdata = symbol.symbolTableIndex;
            }
            else
            {
                op.optype = ProtoCore.DSASM.AddressType.VarIndex;
                op.opdata = symbol.symbolTableIndex;
            }
            return op;
        }

        protected void AllocateVar(ProtoCore.DSASM.SymbolNode symbol)
        {
            symbol.isArray = false;
            if (ProtoCore.DSASM.MemoryRegion.kMemHeap == symbol.memregion)
            {
                SetHeapData(symbol);

                ProtoCore.DSASM.StackValue opDX = new ProtoCore.DSASM.StackValue();
                opDX.optype = ProtoCore.DSASM.AddressType.Register;
                opDX.opdata = (int)ProtoCore.DSASM.Registers.DX;

                ProtoCore.DSASM.StackValue opAllocSize = new ProtoCore.DSASM.StackValue();
                opAllocSize.optype = ProtoCore.DSASM.AddressType.Int;
                opAllocSize.opdata = symbol.size;

                EmitInstrConsole(ProtoCore.DSASM.kw.mov, ProtoCore.DSASM.kw.regDX, opAllocSize.opdata.ToString());
                EmitBinary(ProtoCore.DSASM.OpCode.MOV, opDX, opAllocSize);
            }
            SetStackIndex(symbol);
        }

        protected void SetHeapData(ProtoCore.DSASM.SymbolNode symbol)
        {
            symbol.size = ProtoCore.DSASM.Constants.kPointerSize;
            symbol.heapIndex = core.GlobHeapOffset++;
        }

        protected void SetStackIndex(ProtoCore.DSASM.SymbolNode symbol)
        {
            if (core.ExecMode == ProtoCore.DSASM.InterpreterMode.kExpressionInterpreter)
            {
                //Set the index of the symbol relative to the watching stack
                symbol.index = core.watchBaseOffset;
                core.watchBaseOffset += symbol.size;
                return;
            }

            bool isLanguageBlock = CodeBlockType.kLanguage == codeBlock.blockType;
            int langblockOffset = 0;
            bool isGlobal = null == localProcedure;

            /*
            // Remove this check once the global stackframe push is implemented
            if (isLanguageBlock && 0 != codeBlock.codeBlockId && !isGlobal)
            {
                langblockOffset = ProtoCore.DSASM.StackFrame.kStackFrameSize;
            }
            
             * */

            if (ProtoCore.DSASM.Constants.kGlobalScope != globalClassIndex)
            {
                if (!isGlobal)
                {
                    // Local variable in a member function
                    symbol.index = -1 - ProtoCore.DSASM.StackFrame.kStackFrameSize - langblockOffset - core.BaseOffset;
                    core.BaseOffset += symbol.size;
                }
                else
                {
                    // Member variable: static variable allocated on global
                    // stack
                    if (symbol.isStatic)
                    {
                        symbol.index = core.GlobOffset - langblockOffset;
                        core.GlobOffset += symbol.size;
                    }
                    else
                    {
                        symbol.index = classOffset - langblockOffset;
                        classOffset += symbol.size;
                    }
                }
            }
            else if (!isGlobal)
            {
                // Local variable in a global function
                symbol.index = -1 - ProtoCore.DSASM.StackFrame.kStackFrameSize - langblockOffset - core.BaseOffset;
                core.BaseOffset += symbol.size;
            }
            else
            {
                // Global variable
                symbol.index = core.GlobOffset + langblockOffset;
                core.GlobOffset += symbol.size;
            }
        }

        #region EMIT_INSTRUCTION_TO_CONSOLE
        public void EmitCompileLog(string message)
        {
            if (dumpByteCode && !isAssocOperator && !core.Options.DumpOperatorToMethodByteCode)
            {
                for (int i = 0; i < core.AsmOutputIdents; ++i)
                    core.AsmOutput.Write("\t");
                core.AsmOutput.Write(message);
            }
            
        }
        public void EmitCompileLogFunctionStart(string function)
        {
            if (dumpByteCode && !isAssocOperator && !core.Options.DumpOperatorToMethodByteCode)
            {
                core.AsmOutput.Write(function);
                core.AsmOutput.Write("{\n");
                core.AsmOutputIdents++;
            }
        }

        public void EmitCompileLogFunctionEnd()
        {
            if (dumpByteCode && !isAssocOperator && !core.Options.DumpOperatorToMethodByteCode)
            {
                core.AsmOutputIdents--;
                for (int i = 0; i < core.AsmOutputIdents; ++i)
                    core.AsmOutput.Write("\t");
                core.AsmOutput.Write("}\n\n");
            }
        }

        public void EmitInstrConsole(string instr)
        {
            if (core.Options.DumpOperatorToMethodByteCode == false)
            {
                if (dumpByteCode && !isAssocOperator)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
            else
            {
                if (dumpByteCode)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
        }
        public void EmitInstrConsole(string instr, string op1)
        {
            if (core.Options.DumpOperatorToMethodByteCode == false)
            {
                if (dumpByteCode && !isAssocOperator)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
            else
            {
                if (dumpByteCode)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
        }
        public void EmitInstrConsole(string instr, string op1, string op2)
        {
            if (core.Options.DumpOperatorToMethodByteCode == false)
            {
                if (dumpByteCode && !isAssocOperator)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4} {5}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1, op2);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
            else
            {
                if (dumpByteCode)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4} {5}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1, op2);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
        }
        public void EmitInstrConsole(string instr, string op1, string op2, string op3)
        {
            if (core.Options.DumpOperatorToMethodByteCode == false)
            {
                if (dumpByteCode && !isAssocOperator)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4} {5} {6}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1, op2, op3);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }
            else
            {
                if (dumpByteCode)
                {
                    var str = string.Format("[{0}.{1}.{2}]{3} {4} {5} {6}\n", codeBlock.language == Language.kAssociative ? "a" : "i", codeBlock.codeBlockId, pc, instr, op1, op2, op3);
                    for (int i = 0; i < core.AsmOutputIdents; ++i)
                        core.AsmOutput.Write("\t");
                    core.AsmOutput.Write(str);
                }
            }

        }
        #endregion //   EMIT_INSTRUCTION_TO_CONSOLE

        protected abstract void SetEntry();

        public abstract int Emit(ProtoCore.AST.Node codeblocknode, ProtoCore.AssociativeGraph.GraphNode graphNode = null);

        protected string GetConstructBlockName(string construct)
        {
            string desc = "blockname";
            return blockScope.ToString() + "_" + construct + "_" + desc;
        }

        protected ProtoCore.DSASM.DebugInfo GetDebugObject(int line, int col, int eline, int ecol, int nextStep_a, int nextStep_b = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.DebugInfo debug = null;

            if (core.Options.EmitBreakpoints)
            {
                if ( (core.Options.IDEDebugMode || core.Options.WatchTestMode || core.Options.IsDeltaExecution)
                    && ProtoCore.DSASM.Constants.kInvalidIndex != line
                    && ProtoCore.DSASM.Constants.kInvalidIndex != col)
                {
                    debug = new ProtoCore.DSASM.DebugInfo(line, col, eline, ecol, core.CurrentDSFileName);
                    debug.nextStep.Add(nextStep_a);

                    if (ProtoCore.DSASM.Constants.kInvalidIndex != nextStep_b)
                        debug.nextStep.Add(nextStep_b);
                }
            }

            return debug;
        }

        abstract protected void EmitGetterSetterForIdentList(
            ProtoCore.AST.Node node, 
            ref ProtoCore.Type inferedType, 
            ProtoCore.AssociativeGraph.GraphNode graphNode, 
            ProtoCore.DSASM.AssociativeSubCompilePass subPass, 
            out bool isCollapsed, 
            ProtoCore.AST.Node setterArgument = null
            );


        //  
        //  proc dfsgetsymbollist(node, lefttype, nodeRef)
        //      if node is identlist
        //          dfsemitpushlist(node.left, lefttype, nodeRef)
        //      else if node is identifier
        //          def symbol = veryifyallocation(node.left, lefttype)
        //          if symbol is allocated
        //              lefttype = symbol.type
        //              def updateNode
        //              updateNode.symbol = symbol 
        //              updateNode.isMethod = false
        //              nodeRef.push(updateNode)
        //          end
        //      else if node is function call
        //          def procNode = procTable.GetProc(node)
        //          if procNode is allocated
        //              lefttype = procNode.returntype
        //              def updateNode
        //              updateNode.procNode = procNode
        //              updateNode.isMethod = true
        //              updateNodeRef.push(updateNode)
        //          end
        //      end
        //  end
        //

        public void DFSGetSymbolList(Node pNode, ref ProtoCore.Type lefttype, ProtoCore.AssociativeGraph.UpdateNodeRef nodeRef)
        {
            dynamic node = pNode;
            if (node is ProtoCore.AST.ImperativeAST.IdentifierListNode || node is ProtoCore.AST.AssociativeAST.IdentifierListNode)
            {
                dynamic bnode = node;
                DFSGetSymbolList(bnode.LeftNode, ref lefttype, nodeRef);
                node = bnode.RightNode;
            }
            
            if (node is ProtoCore.AST.ImperativeAST.IdentifierNode || node is ProtoCore.AST.AssociativeAST.IdentifierNode)
            {
                dynamic identnode = node;
                ProtoCore.DSASM.SymbolNode symbolnode = null;

                bool isAccessible = false;
                bool isAllocated = VerifyAllocation(identnode.Value, lefttype.UID, globalProcIndex, out symbolnode, out isAccessible);
                if (isAllocated)
                {
                    if (null == symbolnode)
                    {
                        // It is inaccessible from here due to access modifier.
                        // Just attempt to retrieve the symbol
                        int symindex = core.ClassTable.ClassNodes[lefttype.UID].GetFirstVisibleSymbolNoAccessCheck(identnode.Value);
                        if (ProtoCore.DSASM.Constants.kInvalidIndex != symindex)
                        {
                            symbolnode = core.ClassTable.ClassNodes[lefttype.UID].symbols.symbolList[symindex];
                        }
                    }

                    lefttype = symbolnode.datatype;

                    ProtoCore.AssociativeGraph.UpdateNode updateNode = new AssociativeGraph.UpdateNode();
                    updateNode.symbol = symbolnode;
                    updateNode.nodeType = ProtoCore.AssociativeGraph.UpdateNodeType.kSymbol;
                    nodeRef.PushUpdateNode(updateNode);
                }
                else
                {
                    // Is it a class?
                    int ci = core.ClassTable.IndexOf(identnode.Value);
                    if (ProtoCore.DSASM.Constants.kInvalidIndex != ci)
                    {
                        lefttype.UID = ci;

                        // Comment Jun:
                        // Create a symbol node that contains information about the class type that contains static properties
                        ProtoCore.DSASM.SymbolNode classSymbol = new DSASM.SymbolNode();
                        classSymbol.memregion = DSASM.MemoryRegion.kMemStatic;
                        classSymbol.name = identnode.Value;
                        classSymbol.classScope = ci;

                        ProtoCore.AssociativeGraph.UpdateNode updateNode = new AssociativeGraph.UpdateNode();
                        updateNode.symbol = classSymbol;
                        updateNode.nodeType = ProtoCore.AssociativeGraph.UpdateNodeType.kSymbol;
                        nodeRef.PushUpdateNode(updateNode);

                    }
                    else
                    {
                        // In this case, the lhs type is undefined
                        // Just attempt to create a symbol node
                        string ident = identnode.Value;
                        if (0 != ident.CompareTo(ProtoCore.DSDefinitions.Kw.kw_this))
                        {
                            symbolnode = new SymbolNode();
                            symbolnode.name = identnode.Value;

                            ProtoCore.AssociativeGraph.UpdateNode updateNode = new AssociativeGraph.UpdateNode();
                            updateNode.symbol = symbolnode;
                            updateNode.nodeType = AssociativeGraph.UpdateNodeType.kSymbol;
                            nodeRef.PushUpdateNode(updateNode);
                        }
                    }
                }
            }
            else if (node is ProtoCore.AST.ImperativeAST.FunctionCallNode || node is ProtoCore.AST.AssociativeAST.FunctionCallNode)
            {
                string functionName = node.Function.Value;
                if (ProtoCore.Utils.CoreUtils.IsGetterSetter(functionName))
                {
                    string property;
                    if (CoreUtils.TryGetPropertyName(functionName, out property))
                    {
                        functionName = property;
                    }
                    dynamic identnode = node;
                    ProtoCore.DSASM.SymbolNode symbolnode = null;


                    bool isAccessible = false;
                    bool isAllocated = VerifyAllocation(functionName, lefttype.UID, globalProcIndex, out symbolnode, out isAccessible);
                    if (isAllocated)
                    {
                        if (null == symbolnode)
                        {
                            // It is inaccessible from here due to access modifier.
                            // Just attempt to retrieve the symbol
                            int symindex = core.ClassTable.ClassNodes[lefttype.UID].GetFirstVisibleSymbolNoAccessCheck(functionName);
                            if (ProtoCore.DSASM.Constants.kInvalidIndex != symindex)
                            {
                                symbolnode = core.ClassTable.ClassNodes[lefttype.UID].symbols.symbolList[symindex];
                            }
                        }

                        lefttype = symbolnode.datatype;

                        ProtoCore.AssociativeGraph.UpdateNode updateNode = new AssociativeGraph.UpdateNode();
                        updateNode.symbol = symbolnode;
                        updateNode.nodeType = AssociativeGraph.UpdateNodeType.kSymbol;
                        nodeRef.PushUpdateNode(updateNode);
                    }
                }
                else
                {
                    ProtoCore.AssociativeGraph.UpdateNode updateNode = new AssociativeGraph.UpdateNode();
                    ProtoCore.DSASM.ProcedureNode procNodeDummy = new DSASM.ProcedureNode();
                    procNodeDummy.name = functionName;
                    updateNode.procNode = procNodeDummy;
                    updateNode.nodeType = AssociativeGraph.UpdateNodeType.kMethod;
                    nodeRef.PushUpdateNode(updateNode);
                }
            }
        }

        protected bool DfsEmitIdentList(
            Node pNode, 
            Node parentNode, 
            int contextClassScope, 
            ref ProtoCore.Type lefttype,
            ref int depth, 
            ref ProtoCore.Type finalType, 
            bool isLeftidentList, 
            ref bool isFirstIdent, 
            ref bool isMethodCallPresent,
            ref ProtoCore.DSASM.SymbolNode firstSymbol,
            ProtoCore.AssociativeGraph.GraphNode graphNode = null, 
            ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone,
            ProtoCore.AST.Node binaryExpNode = null)
        {
            bool isRefFromIdentifier = false;

            dynamic node = pNode;
            if (node is ProtoCore.AST.ImperativeAST.IdentifierListNode || node is ProtoCore.AST.AssociativeAST.IdentifierListNode)
            {
                dynamic bnode = node;
                if (ProtoCore.DSASM.Operator.dot != bnode.Optr)
                {
                    string message = "The left hand side of an operation can only contain an indirection operator '.' (48D67B9B)";
                    buildStatus.LogSemanticError(message, core.CurrentDSFileName, bnode.line, bnode.col);
                    throw new BuildHaltException(message);
                }

                isRefFromIdentifier = DfsEmitIdentList(bnode.LeftNode, bnode, contextClassScope, ref lefttype, ref depth, ref finalType, isLeftidentList, ref isFirstIdent, ref isMethodCallPresent, ref firstSymbol, graphNode, subPass);

                if (lefttype.rank > 0)
                {
                    lefttype.UID = finalType.UID = (int)PrimitiveType.kTypeNull;
                    EmitPushNull();
                    return false;
                }
                node = bnode.RightNode;
            }

            if (node is ProtoCore.AST.ImperativeAST.GroupExpressionNode)
            {
                ProtoCore.AST.ImperativeAST.ArrayNode array = node.ArrayDimensions;
                node = node.Expression;
                node.ArrayDimensions = array;
            }
            else if (node is ProtoCore.AST.AssociativeAST.GroupExpressionNode)
            {
                ProtoCore.AST.AssociativeAST.ArrayNode array = node.ArrayDimensions;
                List<ProtoCore.AST.AssociativeAST.AssociativeNode> replicationGuides = node.ReplicationGuides;

                node = node.Expression;
                node.ArrayDimensions = array;
                node.ReplicationGuides = replicationGuides;
            }

            if (node is ProtoCore.AST.ImperativeAST.IdentifierNode || node is ProtoCore.AST.AssociativeAST.IdentifierNode)
            {
                dynamic identnode = node;

                int ci = core.ClassTable.IndexOf(identnode.Value);
                if (ProtoCore.DSASM.Constants.kInvalidIndex != ci)
                {
                    finalType.UID = lefttype.UID = ci;
                }
                else if (identnode.Value == ProtoCore.DSDefinitions.Kw.kw_this)
                {
                    finalType.UID = lefttype.UID = contextClassScope;
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, 0 + "[dim]");
                    ProtoCore.DSASM.StackValue opdim = new ProtoCore.DSASM.StackValue();
                    opdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
                    opdim.opdata = 0;
                    EmitPush(opdim);
                    EmitThisPointerNode();
                    depth++;
                    return true;
                }
                else
                {
                    ProtoCore.DSASM.SymbolNode symbolnode = null;
                    bool isAllocated = false;
                    bool isAccessible = false;
                    if (lefttype.UID != -1)
                    {
                        isAllocated = VerifyAllocation(identnode.Value, lefttype.UID, globalProcIndex, out symbolnode, out isAccessible);
                    }
                    else
                    {
                        isAllocated = VerifyAllocation(identnode.Value, contextClassScope, globalProcIndex, out symbolnode, out isAccessible);
                        Debug.Assert(null == firstSymbol);
                        firstSymbol = symbolnode;
                    }

                    bool callOnClass = false;
                    string leftClassName = "";
                    int leftci = Constants.kInvalidIndex;

                    if (pNode is ProtoCore.AST.ImperativeAST.IdentifierListNode ||
                        pNode is ProtoCore.AST.AssociativeAST.IdentifierListNode)
                    {
                        dynamic leftnode = ((dynamic)pNode).LeftNode;
                        if (leftnode != null && 
                            (leftnode is ProtoCore.AST.ImperativeAST.IdentifierNode ||
                            leftnode is ProtoCore.AST.AssociativeAST.IdentifierNode))
                        {
                            leftClassName = leftnode.Name;
                            leftci = core.ClassTable.IndexOf(leftClassName);
                            if (leftci != ProtoCore.DSASM.Constants.kInvalidIndex)
                            {
                                callOnClass = true;

                                EmitInstrConsole(ProtoCore.DSASM.kw.push, 0 + "[dim]");
                                ProtoCore.DSASM.StackValue dynamicOpdim = new ProtoCore.DSASM.StackValue();
                                dynamicOpdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
                                dynamicOpdim.opdata = 0;
                                EmitPush(dynamicOpdim);

                                EmitInstrConsole(ProtoCore.DSASM.kw.pushm, leftClassName);
                                ProtoCore.DSASM.StackValue classOp = new ProtoCore.DSASM.StackValue();
                                classOp.optype = ProtoCore.DSASM.AddressType.ClassIndex;
                                classOp.opdata = leftci;
                                EmitPushm(classOp, globalClassIndex, codeBlock.codeBlockId);

                                depth = depth + 1;
                            }
                        }
                    }

                    if (null == symbolnode)    //unbound identifier
                    {
                        if (isAllocated && !isAccessible)
                        {
                            string message = String.Format(ProtoCore.BuildData.WarningMessage.kPropertyIsInaccessible, identnode.Value);
                            buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kAccessViolation, message, core.CurrentDSFileName, identnode.line, identnode.col);
                            lefttype.UID = finalType.UID = (int)PrimitiveType.kTypeNull;
                            EmitPushNull();
                            return false;
                        }
                        else
                        {
                            string message = String.Format(ProtoCore.BuildData.WarningMessage.kUnboundIdentifierMsg, identnode.Value);
                            buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kIdUnboundIdentifier, message, core.CurrentDSFileName, identnode.line, identnode.col);
                        }

                        if (depth == 0)
                        {
                            lefttype.UID = finalType.UID = (int)PrimitiveType.kTypeNull;
                            EmitPushNull();
                            depth = 1;
                            return false;
                        }
                        else
                        {
                            DSASM.DyanmicVariableNode dynamicVariableNode = new DSASM.DyanmicVariableNode(identnode.Value, globalProcIndex, globalClassIndex);
                            core.DynamicVariableTable.variableTable.Add(dynamicVariableNode);
                            int dim = 0;
                            if (null != identnode.ArrayDimensions)
                            {
                                dim = DfsEmitArrayIndexHeap(identnode.ArrayDimensions, graphNode);
                            }
                            EmitInstrConsole(ProtoCore.DSASM.kw.push, dim + "[dim]");
                            ProtoCore.DSASM.StackValue dynamicOpdim = new ProtoCore.DSASM.StackValue();
                            dynamicOpdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
                            dynamicOpdim.opdata = dim;
                            EmitPush(dynamicOpdim);

                            EmitInstrConsole(ProtoCore.DSASM.kw.pushm, identnode.Value + "[dynamic]");
                            ProtoCore.DSASM.StackValue dynamicOp = new ProtoCore.DSASM.StackValue();
                            dynamicOp.optype = ProtoCore.DSASM.AddressType.Dynamic;
                            dynamicOp.opdata = core.DynamicVariableTable.variableTable.Count - 1;
                            EmitPushm(dynamicOp, symbolnode == null ? globalClassIndex : symbolnode.classScope, DSASM.Constants.kInvalidIndex);

                            lefttype.UID = finalType.UID = (int)PrimitiveType.kTypeVar;
                            depth++;
                            return true;
                        }
                    }
                    else
                    {
                        if (callOnClass && !symbolnode.isStatic)
                        {
                            string procName = identnode.Name;
                            string property;
                            ProtoCore.DSASM.ProcedureNode staticProcCallNode = core.ClassTable.ClassNodes[leftci].GetFirstStaticMemberFunction(procName);

                            if (null != staticProcCallNode)
                            {
                                string message = String.Format(ProtoCore.BuildData.WarningMessage.kMethodHasInvalidArguments, procName);
                                buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kCallingNonStaticMethodOnClass, message, core.CurrentDSFileName, identnode.line, identnode.col);
                            }
                            else if (CoreUtils.TryGetPropertyName(procName, out property))
                            {
                                string message = String.Format(ProtoCore.BuildData.WarningMessage.kPropertyIsInaccessible, property);
                                buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kCallingNonStaticMethodOnClass, message, core.CurrentDSFileName, identnode.line, identnode.col);
                            }
                            else
                            {
                                string message = String.Format(ProtoCore.BuildData.WarningMessage.kMethodIsInaccessible, procName);
                                buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kCallingNonStaticMethodOnClass, message, core.CurrentDSFileName, identnode.line, identnode.col);
                            }

                            lefttype.UID = finalType.UID = (int)PrimitiveType.kTypeNull;
                            EmitPushNull();
                            return false;
                        }
                    }

                    //
                    // The graph node depends on the first identifier in this identifier list
                    // Where:
                    //      p = f(1);			        
                    //      px = p.x; // px dependent on p
                    //      p = f(2);
                    //
                    if (isFirstIdent && null != graphNode)
                    {
                        isFirstIdent = false;
                        //ProtoCore.AssociativeGraph.GraphNode dependentNode = new ProtoCore.AssociativeGraph.GraphNode();
                        //dependentNode.symbol = symbolnode;
                        //dependentNode.symbolList.Add(symbolnode);
                        //graphNode.PushDependent(dependentNode);
                    }
                    
                    /* Dont try to figure out the type at compile time if it is
                     * an array, it is just not reliable because each element in
                     * an array can have different types
                     */
                    if (!symbolnode.datatype.IsIndexable || symbolnode.datatype.rank < 0)
                        lefttype = symbolnode.datatype;

                    int dimensions = 0;

                    // Get the symbols' table index
                    int runtimeIndex = symbolnode.runtimeTableIndex;

                    ProtoCore.DSASM.AddressType operandType = ProtoCore.DSASM.AddressType.Pointer;

                    if (null != identnode.ArrayDimensions)
                    {
                        dimensions = DfsEmitArrayIndexHeap(identnode.ArrayDimensions, graphNode);
                        operandType = ProtoCore.DSASM.AddressType.ArrayPointer;
                    }

                    if (lefttype.rank >= 0)
                    {
                        lefttype.rank -= dimensions;
                        if (lefttype.rank < 0)
                        {
                            lefttype.rank = 0;
                        }
                    }

                    if (0 == depth || (symbolnode != null && symbolnode.isStatic))
                    {
                        if (ProtoCore.DSASM.Constants.kGlobalScope == symbolnode.functionIndex
                            && ProtoCore.DSASM.Constants.kInvalidIndex != symbolnode.classScope)
                        {
                            // member var
                            operandType = symbolnode.isStatic ? ProtoCore.DSASM.AddressType.StaticMemVarIndex : ProtoCore.DSASM.AddressType.MemVarIndex;
                        }
                        else
                        {
                            operandType = ProtoCore.DSASM.AddressType.VarIndex;
                        }
                    }

                    ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
                    op.optype = operandType;
                    op.opdata = symbolnode.symbolTableIndex;

                    // TODO Jun: Performance. 
                    // Is it faster to have a 'push' specific to arrays to prevent pushing dimension for push instruction?
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, dimensions + "[dim]");
                    ProtoCore.DSASM.StackValue opdim = new ProtoCore.DSASM.StackValue();
                    opdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
                    opdim.opdata = dimensions;
                    EmitPush(opdim);

                    
                    if (isLeftidentList || depth == 0)
                    {
                        EmitInstrConsole(ProtoCore.DSASM.kw.pushm, identnode.Value);
                        EmitPushm(op, symbolnode == null ? globalClassIndex : symbolnode.classScope, runtimeIndex);
                    }
                    else
                    {
                        // change to dynamic call to facilitate update mechanism
                        DSASM.DyanmicVariableNode dynamicVariableNode = new DSASM.DyanmicVariableNode(identnode.Name, globalProcIndex, globalClassIndex);
                        core.DynamicVariableTable.variableTable.Add(dynamicVariableNode);
                        ProtoCore.DSASM.StackValue dynamicOp = new ProtoCore.DSASM.StackValue();
                        dynamicOp.optype = ProtoCore.DSASM.AddressType.Dynamic;
                        dynamicOp.opdata = core.DynamicVariableTable.variableTable.Count - 1;
                        EmitInstrConsole(ProtoCore.DSASM.kw.pushm, identnode.Value + "[dynamic]");
                        EmitPushm(dynamicOp, symbolnode == null ? globalClassIndex : symbolnode.classScope, runtimeIndex);
                    }
                    depth = depth + 1;
                    finalType = lefttype;
                }
                return true;
            }
            else if (node is ProtoCore.AST.ImperativeAST.FunctionCallNode || node is ProtoCore.AST.AssociativeAST.FunctionCallNode)
            {
                // A function call must always track dependents
                bool allowDependents = true;
                if (null != graphNode)
                {
                    allowDependents = graphNode.allowDependents;
                    graphNode.allowDependents = true;
                }

                if (binaryExpNode != null)
                {
                    ProtoCore.Utils.NodeUtils.SetNodeLocation(node, binaryExpNode, binaryExpNode);
                }
                ProtoCore.DSASM.ProcedureNode procnode = TraverseFunctionCall(node, pNode, lefttype.UID, depth, ref finalType, graphNode, subPass, binaryExpNode);

                // Restore the graphNode dependent state
                if (null != graphNode)
                {
                    graphNode.allowDependents = allowDependents;
                }

                // This is the first non-auto generated procedure found in the identifier list
                if (null != procnode)
                {
                    if (!procnode.isConstructor && !procnode.name.Equals(ProtoCore.DSASM.Constants.kStaticPropertiesInitializer))
                    {
                        functionCallStack.Add(procnode);
                        if (null != graphNode)
                        {
                            graphNode.firstProcRefIndex = graphNode.dependentList.Count - 1;
                        }
                    }
                    isMethodCallPresent = !isMethodCallPresent && !procnode.isAutoGenerated && !procnode.isConstructor;
                }

                //finalType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : finalType.UID;
                lefttype = finalType;
                depth = 1;
            }
            else
            {
                string message = "The left side of operator '.' must be an identifier. (B9AEA3A6)";
                buildStatus.LogSemanticError(message, core.CurrentDSFileName, node.line, node.col);
                throw new BuildHaltException(message);
            }
            return false;
        }


        protected int DfsEmitArrayIndexHeap(Node node, AssociativeGraph.GraphNode graphNode = null, ProtoCore.AST.Node parentNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            int indexCnt = 0;
            Debug.Assert(node is ProtoCore.AST.AssociativeAST.ArrayNode || node is ProtoCore.AST.ImperativeAST.ArrayNode);

            IsAssociativeArrayIndexing = true;

            dynamic arrayNode = node;
            while (arrayNode is ProtoCore.AST.AssociativeAST.ArrayNode || arrayNode is ProtoCore.AST.ImperativeAST.ArrayNode)
            {
                ++indexCnt;
                dynamic array = arrayNode;
                ProtoCore.Type lastType = new ProtoCore.Type();
                lastType.UID = (int)PrimitiveType.kTypeVoid;
                lastType.IsIndexable = false;
                DfsTraverse(array.Expr, ref lastType, false, graphNode, subPass, parentNode);
                arrayNode = array.Type;
            }

            IsAssociativeArrayIndexing = false;

            return indexCnt;
        }

#if USE_PREVIOUS_FUNCCALL_TRAVERSAL 
        protected bool VerifyAllocation(string name, int classScope, out ProtoCore.DSASM.SymbolNode node)
        {
            // Identifier scope resolution
            //  1. Current block
            //  2. Outer language blocks
            //  3. Class scope (TODO Jun: Implement checking the class scope)
            //  4. Global scope (Comment Jun: Is there really a global scope? Conceptually, the outer most block is considered global)
            //node = core.GetFirstVisibleSymbol(name, classScope, functionindex, codeBlock);
            node = core.GetFirstVisibleSymbol(name, classScope, procIndex, codeBlock);
            if (null != node)
            {
                return true;
            }
            return false;
        }
#else
        protected bool VerifyAllocation(string name, int classScope, int functionScope, out ProtoCore.DSASM.SymbolNode symbol, out bool isAccessible)
        {
            int symbolIndex = Constants.kInvalidIndex;
            symbol = null;
            isAccessible = false;
            CodeBlock currentCodeBlock = codeBlock;
            if (core.ExecMode == DSASM.InterpreterMode.kExpressionInterpreter)
            {
                int tempBlockId = core.GetCurrentBlockId();
                currentCodeBlock = core.GetCodeBlock(core.CodeBlockList, tempBlockId);
            }

            if (classScope != Constants.kGlobalScope)
            {
                if (IsInLanguageBlockDefinedInFunction())
                {
                    symbolIndex = currentCodeBlock.symbolTable.IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                    if (symbolIndex != Constants.kInvalidIndex)
                    {
                        symbol = currentCodeBlock.symbolTable.symbolList[symbolIndex];
                        isAccessible = true;
                        return true;
                    }
                }

                if ((int)ProtoCore.PrimitiveType.kTypeVoid == classScope)
                {
                    return false;
                }

                if (core.ExecMode == ProtoCore.DSASM.InterpreterMode.kExpressionInterpreter)
                {
                    //Search local variables in the class member function first
                    if (functionScope != Constants.kGlobalScope)
                    {
                        // Aparajit: This function is found to not work well in the expression interpreter as it doesn't return the
                        // correct symbol if the same symbol exists in different contexts such as inside a function defined in a lang block,
                        // inside the lang block itself and in a function in the global scope etc.
                        // TODO: We can later consider replacing GetSymbolInFunction with GetFirstVisibleSymbol consistently in all occurrences 
                        
                        //symbol = core.GetSymbolInFunction(name, classScope, functionScope, currentCodeBlock);
                        symbol = core.GetFirstVisibleSymbol(name, classScope, functionScope, currentCodeBlock);
                        if (symbol != null)
                        {
                            isAccessible = true;
                            return true;
                        }
                    }
                }

                ClassNode thisClass = core.ClassTable.ClassNodes[classScope];

                bool hasThisSymbol;
                AddressType addressType;
                symbolIndex = thisClass.GetSymbolIndex(name, classScope, functionScope, currentCodeBlock.codeBlockId, core, out hasThisSymbol, out addressType);
                if (Constants.kInvalidIndex != symbolIndex)
                {
                    // It is static member, then get node from code block
                    if (AddressType.StaticMemVarIndex == addressType)
                    {
                        symbol = core.CodeBlockList[0].symbolTable.symbolList[symbolIndex];
                    }
                    else
                    {
                        symbol = thisClass.symbols.symbolList[symbolIndex];
                    }

                    isAccessible = true;
                }

                if (hasThisSymbol)
                {
                    return true;
                }
            }
            else
            {
                if (functionScope != Constants.kGlobalScope)
                {
                    // Aparajit: This function is found to not work well in the expression interpreter as it doesn't return the
                    // correct symbol if the same symbol exists in different contexts such as inside a function defined in a lang block,
                    // inside the lang block itself and in a function in the global scope etc.
                    // TODO: We can later consider replacing GetSymbolInFunction with GetFirstVisibleSymbol consistently in all occurrences 
                    
                    //symbol = core.GetSymbolInFunction(name, Constants.kGlobalScope, functionScope, currentCodeBlock);
                    symbol = core.GetFirstVisibleSymbol(name, Constants.kGlobalScope, functionScope, currentCodeBlock);
                    if (symbol != null)
                    {
                        isAccessible = true;
                        return true;
                    }
                }
            }

            CodeBlock searchBlock = currentCodeBlock;
            while (symbolIndex == Constants.kInvalidIndex && searchBlock != null)
            {
                symbolIndex = searchBlock.symbolTable.IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                if (symbolIndex != Constants.kInvalidIndex)
                {
                    symbol = searchBlock.symbolTable.symbolList[symbolIndex];

                    bool ignoreImportedSymbols = !string.IsNullOrEmpty(symbol.ExternLib) && core.IsParsingCodeBlockNode;
                    if (ignoreImportedSymbols)
                    {
                        searchBlock = searchBlock.parent;
                        continue;
                    }
                    isAccessible = true;
                    return true;
                }
                searchBlock = searchBlock.parent;
            }

            return false;
        }

        protected bool VerifyAllocation(string name,string arrayName, int classScope, int functionScope, out ProtoCore.DSASM.SymbolNode symbol, out bool isAccessible)
        {
            int symbolIndex = Constants.kInvalidIndex;
            symbol = null;
            isAccessible = false;

            if (classScope != Constants.kGlobalScope)
            {
                if ((int)ProtoCore.PrimitiveType.kTypeVoid == classScope)
                {
                    return false;
                }
                ClassNode thisClass = core.ClassTable.ClassNodes[classScope];

                bool hasThisSymbol;
                AddressType addressType;
                symbolIndex = thisClass.GetSymbolIndex(name, classScope, functionScope, codeBlock.codeBlockId, core, out hasThisSymbol, out addressType);

                if (Constants.kInvalidIndex != symbolIndex)
                {
                    // It is static member, then get node from code block
                    if (AddressType.StaticMemVarIndex == addressType)
                    {
                        symbol = core.CodeBlockList[0].symbolTable.symbolList[symbolIndex];
                    }
                    else
                    {
                        symbol = thisClass.symbols.symbolList[symbolIndex];
                    }

                    isAccessible = true;
                }

                if (hasThisSymbol)
                {
                    if (symbol != null)
                    {
                        symbol.forArrayName = arrayName;
                    }
                    return true;
                }
                else
                {
                    symbolIndex = codeBlock.symbolTable.IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                    if (symbolIndex != Constants.kInvalidIndex)
                    {
                        symbol = codeBlock.symbolTable.symbolList[symbolIndex];
                        isAccessible = true;
                        if (symbol != null)
                        {
                            symbol.forArrayName = arrayName;
                        }
                        return true;
                    }
                }
            }
            else
            {
                if (functionScope != Constants.kGlobalScope)
                {
                    symbol = core.GetSymbolInFunction(name, Constants.kGlobalScope, functionScope, codeBlock);
                    if (symbol != null)
                    {
                        isAccessible = true;
                         symbol.forArrayName = arrayName;
                        return true;
                    }
                }

                CodeBlock searchBlock = codeBlock;
                while (symbolIndex == Constants.kInvalidIndex && searchBlock != null)
                {
                    symbolIndex = searchBlock.symbolTable.IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                    if (symbolIndex != Constants.kInvalidIndex)
                    {
                        symbol = searchBlock.symbolTable.symbolList[symbolIndex];

                        bool ignoreImportedSymbols = !string.IsNullOrEmpty(symbol.ExternLib) && core.IsParsingCodeBlockNode;
                        if (ignoreImportedSymbols)
                        {
                            searchBlock = searchBlock.parent;
                            continue;
                        }

                        isAccessible = true;
                        if (symbol != null)
                        {
                            symbol.forArrayName = arrayName;
                        }
                        return true;
                    }
                    searchBlock = searchBlock.parent;
                }

                //Fix IDE-448
                //Search current running block as well.
                searchBlock = core.GetCodeBlock(core.CodeBlockList, core.RunningBlock);
                symbolIndex = searchBlock.symbolTable.IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                if (symbolIndex != Constants.kInvalidIndex)
                {
                    symbol = searchBlock.symbolTable.symbolList[symbolIndex];

                    if (symbol != null)
                    {
                        symbol.forArrayName = arrayName;
                    }

                    bool ignoreImportedSymbols = !string.IsNullOrEmpty(symbol.ExternLib) && core.IsParsingCodeBlockNode;
                    if (ignoreImportedSymbols)
                    {
                        return false;
                    }

                    isAccessible = true;                    
                    return true;
                }
            }
            if (symbol != null)
            {
                symbol.forArrayName = arrayName;
            }
            return false;
        }

        protected bool IsProperty(string name)
        {
            if (globalClassIndex == ProtoCore.DSASM.Constants.kInvalidIndex)
            {
                return false;
            }

            bool hasThisSymbol;
            ProtoCore.DSASM.AddressType addressType;
            ProtoCore.DSASM.ClassNode classnode = core.ClassTable.ClassNodes[globalClassIndex];

            int symbolIndex = classnode.GetSymbolIndex(name, globalClassIndex, globalProcIndex, core.RunningBlock, core, out hasThisSymbol, out addressType);
            if (symbolIndex == ProtoCore.DSASM.Constants.kInvalidIndex)
            {
                return false;
            }

            return (classnode.symbols.symbolList[symbolIndex].functionIndex == ProtoCore.DSASM.Constants.kGlobalScope);
        }
#endif

        protected void Backpatch(int bp, int pc)
        {
            if (ProtoCore.DSASM.OpCode.JMP == codeBlock.instrStream.instrList[bp].opCode
                && ProtoCore.DSASM.AddressType.LabelIndex == codeBlock.instrStream.instrList[bp].op1.optype)
            {
                Debug.Assert(ProtoCore.DSASM.Constants.kInvalidIndex == codeBlock.instrStream.instrList[bp].op1.opdata);
                codeBlock.instrStream.instrList[bp].op1.opdata = pc;
            }
            else if (ProtoCore.DSASM.OpCode.CJMP == codeBlock.instrStream.instrList[bp].opCode
                && ProtoCore.DSASM.AddressType.LabelIndex == codeBlock.instrStream.instrList[bp].op3.optype)
            {
                Debug.Assert(ProtoCore.DSASM.Constants.kInvalidIndex == codeBlock.instrStream.instrList[bp].op3.opdata);
                codeBlock.instrStream.instrList[bp].op3.opdata = pc;
            }
        }

        protected void Backpatch(List<BackpatchNode> table, int pc)
        {
            foreach (BackpatchNode node in table)
            {
                Backpatch(node.bp, pc);
            }
        }

        public abstract ProtoCore.DSASM.ProcedureNode TraverseFunctionCall(Node node, Node parentNode, int lefttype, int depth, ref ProtoCore.Type inferedType, ProtoCore.AssociativeGraph.GraphNode graphNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone, ProtoCore.AST.Node bnode = null);

        protected void EmitAlloc(int symbol)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.ALLOC;

            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Int;
            op.opdata = symbol;
            instr.op1 = op;

            ++pc;
            AppendInstruction(instr);
        }


        protected void EmitBounceIntrinsic(int blockId, int entry)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.BOUNCE;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            op1.opdata = blockId;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.Int;
            op2.opdata = entry;
            instr.op2 = op2;

            ++pc;
            AppendInstruction(instr);
        }


        protected void EmitCall(int fi, int type, int depth, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int endLine = ProtoCore.DSASM.Constants.kInvalidIndex, int endCol = ProtoCore.DSASM.Constants.kInvalidIndex,
            int entrypoint = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.CALLR;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.FunctionIndex;
            op1.opdata = fi;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = type;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.Int;
            op3.opdata = depth;
            instr.op3 = op3;

            ++pc;
            instr.debug = GetDebugObject(line, col, endLine, endCol, entrypoint);
            AppendInstruction(instr, line, col);
        }

        protected void EmitCallBaseCtor(int fi, int ci, int offset)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.CALL;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.FunctionIndex;
            op1.opdata = fi;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = ci;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.Int;
            op3.opdata = offset;
            instr.op3 = op3;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitCallSetter(int fi, int ci, 
            int line = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int endLine = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int endCol = ProtoCore.DSASM.Constants.kInvalidIndex,
            int entrypoint = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.CALL;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.FunctionIndex;
            op1.opdata = fi;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = ci;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.Int;
            op3.opdata = 0;
            instr.op3 = op3;

            ++pc;
            instr.debug = GetDebugObject(line, col, endLine, endCol, entrypoint);
            AppendInstruction(instr, line, col);
        }

        protected void EmitDynamicCall(int functionIndex, int type, int depth, 
            int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int entrypoint = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.CALLR;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.Dynamic;
            op1.opdata = functionIndex;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = type;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.Int;
            op3.opdata = depth;
            instr.op3 = op3;

            ++pc;
            instr.debug = GetDebugObject(line, col, endline, endcol, entrypoint);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJmp(int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.jmp, " L1(" + L1 + ")");

            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JMP;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            op1.opdata = L1;
            instr.op1 = op1;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJleq(ProtoCore.DSASM.StackValue op1, ProtoCore.DSASM.StackValue op2, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JMP_LTEQ;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = op2;
            instr.op3 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJgeq(ProtoCore.DSASM.StackValue op1, ProtoCore.DSASM.StackValue op2, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JMP_GTEQ;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = op2;
            instr.op3 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJgz(ProtoCore.DSASM.StackValue op1, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JGZ;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJlz(ProtoCore.DSASM.StackValue op1, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JLZ;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJz(ProtoCore.DSASM.StackValue op1, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JZ;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitJle(ProtoCore.DSASM.StackValue op1, ProtoCore.DSASM.StackValue op2, int L1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.JMP_LT;

            ProtoCore.DSASM.StackValue dest = new ProtoCore.DSASM.StackValue();
            dest.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            dest.opdata = L1;

            instr.op1 = op1;
            instr.op2 = op2;
            instr.op3 = dest;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, L1);
            AppendInstruction(instr, line, col);
        }

        protected void EmitCJmp(int L1, int L2, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.cjmp, ProtoCore.DSASM.kw.regCX + " L1(" + L1 + ") L2(" + L2 + ")");

            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.CJMP;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.Register;
            op1.opdata = (int)ProtoCore.DSASM.Registers.CX;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            op2.opdata = L1;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.LabelIndex;
            op3.opdata = L2;
            instr.op3 = op3;

            ++pc;
            if (core.DebugProps.breakOptions.HasFlag(DebugProperties.BreakpointOptions.EmitInlineConditionalBreakpoint))
            {
                instr.debug = null;
            }
            else
                instr.debug = GetDebugObject(line, col, eline, ecol, L1, L2);

            AppendInstruction(instr, line, col);
        }

        protected void EmitPopArray(int size)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.ALLOCA;

            instr.op1 = StackUtils.BuildInt(size);
            instr.op2 = StackUtils.BuildArrayPointer(0);

            ++pc;
            AppendInstruction(instr);
        }


        protected void EmitPopGuide()
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POPG;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPushArrayIndex(int dimCount)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHINDEX;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.ArrayDim;
            op1.opdata = dimCount;
            instr.op1 = op1;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPushReplicationGuide(int replicationGuide)
        {
            SetEntry();
            
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHINDEX;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
            op1.opdata = replicationGuide;
            instr.op1 = op1;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPopString(int size)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.ALLOCA;

            instr.op1 = StackUtils.BuildInt(size);
            instr.op2 = StackUtils.BuildString(0);

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPop(ProtoCore.DSASM.StackValue op, int classIndex, 
            int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POP;
            instr.op1 = op;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = classIndex;
            instr.op2 = op2;


            // For debugging, assert here but these should raise runtime errors in the VM
            Debug.Assert(ProtoCore.DSASM.AddressType.VarIndex == op.optype
                || ProtoCore.DSASM.AddressType.MemVarIndex == op.optype
                || ProtoCore.DSASM.AddressType.Register == op.optype);

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, pc);
            AppendInstruction(instr, line, col);
        }

        protected void EmitPopForSymbol(SymbolNode symbol,
            int line = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, 
            int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            Debug.Assert(symbol != null);
            if (symbol == null)
            {
                return;
            }

            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POP;
            instr.op1 = BuildOperand(symbol);

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = symbol.classScope;
            instr.op2 = op2;

            ++pc;

            bool outputBreakpoint = false;
            DebugProperties.BreakpointOptions options = core.DebugProps.breakOptions;
            if (options.HasFlag(DebugProperties.BreakpointOptions.EmitPopForTempBreakpoint))
                outputBreakpoint = true;

            // Do not emit breakpoints for null or var type declarations
            if (!core.DebugProps.breakOptions.HasFlag(DebugProperties.BreakpointOptions.SuppressNullVarDeclarationBreakpoint))
            {
                // Don't need no pop for temp (unless caller demands it).
                if (outputBreakpoint || !symbol.name.StartsWith("%"))
                    instr.debug = GetDebugObject(line, col, eline, ecol, pc);
            }
            AppendInstruction(instr, line, col);
        }

        protected void EmitPopForSymbolW(SymbolNode symbol,
            int line = ProtoCore.DSASM.Constants.kInvalidIndex,
            int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex,
            int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            Debug.Assert(symbol != null);
            if (symbol == null)
            {
                return;
            }

            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POPW;
            instr.op1 = BuildOperand(symbol);

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = symbol.classScope;
            instr.op2 = op2;

            ++pc;

            AppendInstruction(instr);
        }

        // TODO Jun: Merge EmitPopList with the associative version and implement in a codegen utils file
        protected void EmitPopList(int depth, int startscope, int block, 
            int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POPLIST;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.Int;
            op1.opdata = depth;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.Int;
            op2.opdata = startscope;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            op3.opdata = block;
            instr.op3 = op3;

            ++pc;
            instr.debug = GetDebugObject(line, col, endline, endcol, pc);
            AppendInstruction(instr, line, col);
        }

        protected void EmitPushBlockID(int blockID)
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.pushb, blockID.ToString());
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHB;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.Int;
            op1.opdata = blockID;
            instr.op1 = op1;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPopBlockID()
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.popb);
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POPB;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPushList(int depth, int startscope, int block, bool fromDotCall = false)
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHLIST;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            if (fromDotCall)
            {
                op1.optype = ProtoCore.DSASM.AddressType.Dynamic;
            }
            else
            {
                op1.optype = ProtoCore.DSASM.AddressType.Int;
            }
            op1.opdata = depth;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = startscope;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            op3.opdata = block;
            instr.op3 = op3;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitPush(ProtoCore.DSASM.StackValue op, int rank = 0, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSH;
            instr.op1 = op;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = globalClassIndex;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.ArrayDim;
            op3.opdata = rank;
            instr.op3 = op3;

            ++pc;
            AppendInstruction(instr, line, col);
        }

        protected void EmitPushG(ProtoCore.DSASM.StackValue op, int rank = 0, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHG;
            instr.op1 = op;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = globalClassIndex;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.ArrayDim;
            op3.opdata = rank;
            instr.op3 = op3;

            ++pc;
            AppendInstruction(instr, line, col);
        }


        protected void EmitPushType(int UID, int rank)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSH;

            ProtoCore.DSASM.StackValue op1 = new StackValue();
            op1.optype = AddressType.StaticType;
            op1.metaData = new MetaData { type = UID };
            op1.opdata = rank;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = globalClassIndex;
            instr.op2 = op2;

            ++pc;
            AppendInstruction(instr);
        }

        private void AppendInstruction(Instruction instr, int line = Constants.kInvalidIndex, int col = Constants.kInvalidIndex)
        {
            if (DSASM.InterpreterMode.kExpressionInterpreter == core.ExecMode)
            {
                core.ExprInterpreterExe.iStreamCanvas.instrList.Add(instr);
            }
            else if(!core.IsParsingCodeBlockNode && !core.IsParsingPreloadedAssembly)
            {
                codeBlock.instrStream.instrList.Add(instr);

                if(line > 0 && col > 0)
                    updatePcDictionary(line, col);
            }
        }

        protected void EmitPushForSymbol(SymbolNode symbol, ProtoCore.AST.Node identNode)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSH;
            instr.op1 = BuildOperand(symbol);

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = symbol.classScope;
            instr.op2 = op2;

            ++pc;

            DebugProperties.BreakpointOptions options = core.DebugProps.breakOptions;
            if (options.HasFlag(DebugProperties.BreakpointOptions.EmitIdentifierBreakpoint))
            {
                instr.debug = GetDebugObject(identNode.line, identNode.col,
                    identNode.endLine, identNode.endCol, pc);
            }

            AppendInstruction(instr, identNode.line, identNode.col);
        }

        protected void EmitPushForSymbolW(SymbolNode symbol, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHW;
            instr.op1 = BuildOperand(symbol);

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = symbol.classScope;
            instr.op2 = op2;

            ++pc;
            //instr.debug = GetDebugObject(line, col, pc);
            AppendInstruction(instr);
        }


        protected void EmitPushForSymbolGuide(SymbolNode symbol, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHG;
            instr.op1 = BuildOperand(symbol);

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = symbol.classScope;
            instr.op2 = op2;

            ++pc;
            //instr.debug = GetDebugObject(line, col, pc);
            AppendInstruction(instr);
        }
        
        protected void EmitPushm(ProtoCore.DSASM.StackValue op, int classIndex, int blockId, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.PUSHM;
            instr.op1 = op;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = classIndex;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            op3.opdata = blockId;
            instr.op3 = op3;

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, pc);
            AppendInstruction(instr, line, col);
        }

        protected void EmitPopm(ProtoCore.DSASM.StackValue op, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.POPM;
            instr.op1 = op;

            ++pc;
            if (emitDebugInfo)
            {
                instr.debug = GetDebugObject(line, col, endline, endcol, pc);
            }
            AppendInstruction(instr, line, col);
        }

        protected void EmitPushNull()
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.push, ProtoCore.DSASM.Literal.Null);
            EmitPush(StackUtils.BuildNull());
        }

        protected void EmitMov(ProtoCore.DSASM.StackValue opDest, ProtoCore.DSASM.StackValue opSrc)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.MOV;
            instr.op1 = opDest;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = globalClassIndex;
            instr.op2 = opSrc;

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitThrow()
        {
            SetEntry();

            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.THROW;

            ProtoCore.DSASM.StackValue op1 = new ProtoCore.DSASM.StackValue();
            op1.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            op1.opdata = codeBlock.codeBlockId;
            instr.op1 = op1;

            ProtoCore.DSASM.StackValue op2 = new ProtoCore.DSASM.StackValue();
            op2.optype = ProtoCore.DSASM.AddressType.ClassIndex;
            op2.opdata = globalClassIndex;
            instr.op2 = op2;

            ProtoCore.DSASM.StackValue op3 = new ProtoCore.DSASM.StackValue();
            op3.optype = ProtoCore.DSASM.AddressType.FunctionIndex;
            op3.opdata = globalProcIndex;
            instr.op3 = op3;

            ++pc;
            AppendInstruction(instr);
        }

        protected abstract void EmitRetb(int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
             int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex);
			
		protected abstract void EmitRetcn(int blockId = Constants.kInvalidIndex, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
             int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex);
        
        protected abstract void EmitReturn(int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
             int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex);

        protected void EmitReturnToRegister(int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int endline = ProtoCore.DSASM.Constants.kInvalidIndex, int endcol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            // Pop to the rx register
            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Register;
            op.opdata = (int)ProtoCore.DSASM.Registers.RX;
            EmitInstrConsole(ProtoCore.DSASM.kw.pop, ProtoCore.DSASM.kw.regRX);
            EmitPop(op, Constants.kGlobalScope, line, col, endline, endcol);

            // Emit the ret instruction only if this is a function we are returning from
            if (core.IsFunctionCodeBlock(codeBlock))
            {
                // Emit the return isntruction to terminate the function
                EmitInstrConsole(ProtoCore.DSASM.kw.ret);
                EmitReturn(line, col, endline, endcol);
            }
            else
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.retb);
                EmitRetb( line, col, endline, endcol);
            }
        }

        protected void EmitBinary(ProtoCore.DSASM.OpCode opcode, ProtoCore.DSASM.StackValue op1, ProtoCore.DSASM.StackValue op2, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = opcode;
            instr.op1 = op1;
            instr.op2 = op2;

            // For debugging, assert here but these should raise runtime errors in the VM
            Debug.Assert(ProtoCore.DSASM.AddressType.VarIndex == op1.optype || ProtoCore.DSASM.AddressType.Register == op1.optype);

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, pc);
            AppendInstruction(instr, line, col);
        }

        protected void EmitUnary(ProtoCore.DSASM.OpCode opcode, ProtoCore.DSASM.StackValue op1, int line = ProtoCore.DSASM.Constants.kInvalidIndex, int col = ProtoCore.DSASM.Constants.kInvalidIndex,
            int eline = ProtoCore.DSASM.Constants.kInvalidIndex, int ecol = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            SetEntry();
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = opcode;
            instr.op1 = op1;

            // For debugging, assert here but these should raise runtime errors in the VM
            Debug.Assert(ProtoCore.DSASM.AddressType.VarIndex == op1.optype || ProtoCore.DSASM.AddressType.Register == op1.optype);

            ++pc;
            instr.debug = GetDebugObject(line, col, eline, ecol, pc);
            AppendInstruction(instr, line, col);
        }

        protected void EmitIntNode(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.AssociativeGraph.GraphNode graphNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier) 
            {
                return;
            }

            dynamic iNode = node;
            if (!enforceTypeCheck || core.TypeSystem.IsHigherRank((int)PrimitiveType.kTypeInt, inferedType.UID))
            {
                inferedType.UID = (int)PrimitiveType.kTypeInt;
            }
            
            inferedType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : inferedType.UID;


            if (core.Options.TempReplicationGuideEmptyFlag)
            {
                if (emitReplicationGuide)
                {
                    int replicationGuides = 0;
                    
                    // Push the number of guides
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, replicationGuides + "[guide]");
                    ProtoCore.DSASM.StackValue opNumGuides = new ProtoCore.DSASM.StackValue();
                    opNumGuides.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
                    opNumGuides.opdata = replicationGuides;
                    EmitPush(opNumGuides);
                }
            }


            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Int;
            try
            {
                op.opdata = System.Convert.ToInt64(iNode.value);
                op.opdata_d = System.Convert.ToDouble(iNode.value, cultureInfo);
            }
            catch (System.OverflowException)
            {
                buildStatus.LogSemanticError("The value is too big or too small to be converted to an integer", core.CurrentDSFileName, node.line, node.col);
            }

            if (core.Options.TempReplicationGuideEmptyFlag && emitReplicationGuide)
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.pushg, iNode.value);
                EmitPushG(op, iNode.line, iNode.col);
            }
            else
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.push, iNode.value);
                EmitPush(op, iNode.line, iNode.col);
            }

            if (IsAssociativeArrayIndexing)
            {
                if (null != graphNode)
                {
                    // Get the last dependent which is the current identifier being indexed into
                    SymbolNode literalSymbol = new SymbolNode();
                    literalSymbol.name = iNode.value;

                    AssociativeGraph.UpdateNode intNode = new AssociativeGraph.UpdateNode();
                    intNode.symbol = literalSymbol;
                    intNode.nodeType = AssociativeGraph.UpdateNodeType.kLiteral;

                    if (graphNode.isIndexingLHS)
                    {
                        graphNode.dimensionNodeList.Add(intNode);
                    }
                    else
                    {
                        int lastDependentIndex = graphNode.dependentList.Count - 1;
                        if (lastDependentIndex >= 0)
                        {
                            ProtoCore.AssociativeGraph.UpdateNode currentDependentNode = graphNode.dependentList[lastDependentIndex].updateNodeRefList[0].nodeList[0];
                            currentDependentNode.dimensionNodeList.Add(intNode);

                            if (core.Options.FullSSA)
                            {
                                if (null != firstSSAGraphNode)
                                {
                                    lastDependentIndex = firstSSAGraphNode.dependentList.Count - 1;
                                    ProtoCore.AssociativeGraph.UpdateNode firstSSAUpdateNode = firstSSAGraphNode.dependentList[lastDependentIndex].updateNodeRefList[0].nodeList[0];
                                    firstSSAUpdateNode.dimensionNodeList.Add(intNode);
                                }
                            }
                        }
                    }
                }
            }
        }

        protected void EmitCharNode(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            dynamic cNode = node;
            if (!enforceTypeCheck || core.TypeSystem.IsHigherRank((int)PrimitiveType.kTypeChar, inferedType.UID))
            {
                inferedType.UID = (int)PrimitiveType.kTypeChar;
            }
            inferedType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : inferedType.UID;

            Byte[] utf8bytes = ProtoCore.Utils.EncodingUtils.UTF8StringToUTF8Bytes((String)cNode.value);
            String value = Encoding.UTF8.GetString(utf8bytes);
            if (value.Length > 1)
            {
                buildStatus.LogSyntaxError("Too many characters in character literal", null, node.line, node.col);
            }
  
            String strValue = "'" + value + "'";
            EmitInstrConsole(ProtoCore.DSASM.kw.push, strValue);

            ProtoCore.DSASM.StackValue op = ProtoCore.DSASM.StackUtils.BuildChar(value[0]);
            EmitPush(op, cNode.line, cNode.col);
        }
       
        protected void EmitStringNode(Node node, ref ProtoCore.Type inferedType, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            dynamic sNode = node;
            if (!enforceTypeCheck || core.TypeSystem.IsHigherRank((int)PrimitiveType.kTypeString, inferedType.UID))
            {
                inferedType.UID = (int)PrimitiveType.kTypeString;
            }

            Byte[] utf8bytes = ProtoCore.Utils.EncodingUtils.UTF8StringToUTF8Bytes((String)sNode.value);
            String value = Encoding.UTF8.GetString(utf8bytes);

            foreach (char ch in value)
            {
                String strValue = "'" + ch + "'";
                EmitInstrConsole(ProtoCore.DSASM.kw.push, strValue);

                ProtoCore.DSASM.StackValue op = ProtoCore.DSASM.StackUtils.BuildChar(ch);
                EmitPush(op, node.line, node.col);
            }

            EmitInstrConsole(ProtoCore.DSASM.kw.alloca, value.Length.ToString());
            EmitPopString(value.Length);
        }
        
        protected void EmitDoubleNode(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.AssociativeGraph.GraphNode graphNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            dynamic dNode = node;
            if (!enforceTypeCheck || core.TypeSystem.IsHigherRank((int)PrimitiveType.kTypeDouble, inferedType.UID))
            {
                inferedType.UID = (int)PrimitiveType.kTypeDouble;
            }
            inferedType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : inferedType.UID;

            if (core.Options.TempReplicationGuideEmptyFlag)
            {
                if (emitReplicationGuide)
                {
                    int replicationGuides = 0;

                    // Push the number of guides
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, replicationGuides + "[guide]");
                    ProtoCore.DSASM.StackValue opNumGuides = new ProtoCore.DSASM.StackValue();
                    opNumGuides.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
                    opNumGuides.opdata = replicationGuides;
                    EmitPush(opNumGuides);
                }
            }

            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Double;
            op.opdata = (Int64)System.Convert.ToDouble(dNode.value);
            op.opdata_d = System.Convert.ToDouble(dNode.value, cultureInfo);

            if (core.Options.TempReplicationGuideEmptyFlag && emitReplicationGuide)
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.pushg, dNode.value);
                EmitPushG(op, dNode.line, dNode.col);
            }
            else
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.push, dNode.value);
                EmitPush(op, dNode.line, dNode.col);
            }

            if (IsAssociativeArrayIndexing)
            {
                if (null != graphNode)
                {
                    // Get the last dependent which is the current identifier being indexed into
                    SymbolNode literalSymbol = new SymbolNode();
                    literalSymbol.name = dNode.value;

                    AssociativeGraph.UpdateNode intNode = new AssociativeGraph.UpdateNode();
                    intNode.symbol = literalSymbol;
                    intNode.nodeType = AssociativeGraph.UpdateNodeType.kLiteral;

                    if (graphNode.isIndexingLHS)
                    {
                        graphNode.dimensionNodeList.Add(intNode);
                    }
                    else
                    {
                        int lastDependentIndex = graphNode.dependentList.Count - 1;
                        ProtoCore.AssociativeGraph.UpdateNode currentDependentNode = graphNode.dependentList[lastDependentIndex].updateNodeRefList[0].nodeList[0];
                        currentDependentNode.dimensionNodeList.Add(intNode);

                        if (core.Options.FullSSA)
                        {
                            if (null != firstSSAGraphNode)
                            {
                                lastDependentIndex = firstSSAGraphNode.dependentList.Count - 1;
                                ProtoCore.AssociativeGraph.UpdateNode firstSSAUpdateNode = firstSSAGraphNode.dependentList[lastDependentIndex].updateNodeRefList[0].nodeList[0];
                                firstSSAUpdateNode.dimensionNodeList.Add(intNode);
                            }
                        }
                    }
                }
            }
        }

        protected void EmitBooleanNode(Node node, ref ProtoCore.Type inferedType, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            dynamic bNode = node;
            // We need to get inferedType for boolean variable so that we can perform type check
            if (enforceTypeCheck || core.TypeSystem.IsHigherRank((int)PrimitiveType.kTypeBool, inferedType.UID))
            {
                inferedType.UID = (int)PrimitiveType.kTypeBool;
            }

            if (core.Options.TempReplicationGuideEmptyFlag)
            {
                if (emitReplicationGuide)
                {
                    int replicationGuides = 0;

                    // Push the number of guides
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, replicationGuides + "[guide]");
                    ProtoCore.DSASM.StackValue opNumGuides = new ProtoCore.DSASM.StackValue();
                    opNumGuides.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
                    opNumGuides.opdata = replicationGuides;
                    EmitPush(opNumGuides);
                }
            }

            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Boolean;
            op.opdata = 1;

            if (0 == bNode.value.CompareTo("false"))
            {
                op.opdata = 0;
            }

            if (core.Options.TempReplicationGuideEmptyFlag && emitReplicationGuide)
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.pushg, bNode.value);
                EmitPushG(op, bNode.line, bNode.col);
            }
            else
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.push, bNode.value);
                EmitPush(op, bNode.line, bNode.col);
            }
        }

        protected void EmitNullNode(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            dynamic nullNode = node;
            inferedType.UID = (int)PrimitiveType.kTypeNull;

            inferedType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : inferedType.UID;

            if (core.Options.TempReplicationGuideEmptyFlag)
            {
                if (emitReplicationGuide)
                {
                    int replicationGuides = 0;

                    // Push the number of guides
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, replicationGuides + "[guide]");
                    ProtoCore.DSASM.StackValue opNumGuides = new ProtoCore.DSASM.StackValue();
                    opNumGuides.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
                    opNumGuides.opdata = replicationGuides;
                    EmitPush(opNumGuides);
                }
            }


            ProtoCore.DSASM.StackValue op = StackUtils.BuildNull();

            if (core.Options.TempReplicationGuideEmptyFlag && emitReplicationGuide)
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.pushg, ProtoCore.DSASM.Literal.Null);
                EmitPushG(op, nullNode.line, nullNode.col);
            }
            else
            {
                EmitInstrConsole(ProtoCore.DSASM.kw.push, ProtoCore.DSASM.Literal.Null);
                EmitPush(op, nullNode.line, nullNode.col);
            }
        }

        protected void EmitReturnNode(Node node)
        {
            throw new NotImplementedException();
        }

        protected int EmitReplicationGuides(List<ProtoCore.AST.AssociativeAST.AssociativeNode> replicationGuidesList, bool emitNumber = false)
        {
            int replicationGuides = 0;
            if (null != replicationGuidesList && replicationGuidesList.Count > 0)
            {
                replicationGuides = replicationGuidesList.Count;
                for (int n = 0; n < replicationGuides; ++n)
                {
                    ProtoCore.DSASM.StackValue opguide = new ProtoCore.DSASM.StackValue();
                    Debug.Assert(replicationGuidesList[n] is ProtoCore.AST.AssociativeAST.IdentifierNode);
                    ProtoCore.AST.AssociativeAST.IdentifierNode nodeGuide = replicationGuidesList[n] as ProtoCore.AST.AssociativeAST.IdentifierNode;

                    EmitInstrConsole(ProtoCore.DSASM.kw.push, nodeGuide.Value);
                    opguide.optype = ProtoCore.DSASM.AddressType.Int;
                    opguide.opdata = System.Convert.ToInt64(nodeGuide.Value);
                    EmitPush(opguide);
                }

                if (emitNumber)
                {
                    EmitInstrConsole(ProtoCore.DSASM.kw.push, replicationGuides + "[guide]");
                    ProtoCore.DSASM.StackValue opNumGuides = new ProtoCore.DSASM.StackValue();
                    opNumGuides.optype = ProtoCore.DSASM.AddressType.ReplicationGuide;
                    opNumGuides.opdata = replicationGuides;
                    EmitPush(opNumGuides);
                }
            }
            
            return replicationGuides; 
        }


        protected void EmitExprListNode(Node node, ref ProtoCore.Type inferedType, ProtoCore.AssociativeGraph.GraphNode graphNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone, ProtoCore.AST.Node parentNode = null)
        {
            dynamic exprlist = node;
            int rank = 0;

            if (subPass != DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                //get the rank
                dynamic ltNode = exprlist;

                bool isExprListNode = (ltNode is ProtoCore.AST.ImperativeAST.ExprListNode || ltNode is ProtoCore.AST.AssociativeAST.ExprListNode);
                bool isStringNode = (ltNode is ProtoCore.AST.ImperativeAST.StringNode || ltNode is ProtoCore.AST.AssociativeAST.StringNode);
                while ((isExprListNode && ltNode.list.Count > 0) || isStringNode)
                {
                    rank++;
                    if (isStringNode)
                        break;

                    ltNode = ltNode.list[0];
                    isExprListNode = (ltNode is ProtoCore.AST.ImperativeAST.ExprListNode || ltNode is ProtoCore.AST.AssociativeAST.ExprListNode);
                    isStringNode = (ltNode is ProtoCore.AST.ImperativeAST.StringNode || ltNode is ProtoCore.AST.AssociativeAST.StringNode);
                }
            }

            int commonType = (int)PrimitiveType.kTypeVoid;
            foreach (Node listNode in exprlist.list)
            {
                bool emitReplicationGuideFlag = emitReplicationGuide;
                emitReplicationGuide = false;

                DfsTraverse(listNode, ref inferedType, false, graphNode, subPass, parentNode);
                if ((int)PrimitiveType.kTypeVoid== commonType)
                {
                    commonType = inferedType.UID;
                }
                else 
                {
                    if (inferedType.UID != commonType)
                    {
                        commonType = (int)PrimitiveType.kTypeVar;
                    }
                }

                emitReplicationGuide = emitReplicationGuideFlag;
            }

            inferedType.UID = commonType;
            inferedType.IsIndexable = true;
            inferedType.rank = rank;

            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            EmitInstrConsole(ProtoCore.DSASM.kw.alloca, exprlist.list.Count.ToString());
            EmitPopArray(exprlist.list.Count);

            if (exprlist.ArrayDimensions != null)
            {
                int dimensions = DfsEmitArrayIndexHeap(exprlist.ArrayDimensions, graphNode);
                EmitInstrConsole(ProtoCore.DSASM.kw.pushindex, dimensions.ToString() + "[dim]");
                EmitPushArrayIndex(dimensions);
            }

            if (core.Options.TempReplicationGuideEmptyFlag && emitReplicationGuide)
            {
                if (node is ProtoCore.AST.AssociativeAST.ExprListNode)
                {
                    var exprNode = node as ProtoCore.AST.AssociativeAST.ExprListNode;
                    int guides = EmitReplicationGuides(exprNode.ReplicationGuides);
                    EmitInstrConsole(ProtoCore.DSASM.kw.pushindex, guides + "[guide]");
                    EmitPushReplicationGuide(guides);
                }
            }
        }

        protected void EmitReturnStatement(Node node, Type inferedType)
        {
            // Check the returned type against the declared return type
            if (null != localProcedure && core.IsFunctionCodeBlock(codeBlock))
            {
                if (localProcedure.isConstructor)
                {
                    buildStatus.LogSemanticError("return statements are not allowed in constructors", core.CurrentDSFileName, node.line, node.col);
                }
                else
                {
#if STATIC_TYPE_CHECKING
                    if (inferedType.UID == (int)PrimitiveType.kInvalidType)
                    {
                        EmitPushNull();
                        EmitReturnToRegister(node.line, node.col, node.endLine, node.endCol);
                        return;
                    }

                    ProtoCore.DSASM.ClassNode typeNode = core.classTable.list[inferedType.UID];
                    Debug.Assert(null != typeNode);

                    bool diableRankCheck = localProcedure.returntype.UID == (int)PrimitiveType.kTypeVar || localProcedure.returntype.rank == -1 || inferedType.UID == (int)PrimitiveType.kTypeVar || inferedType.rank == -1;
                    bool notMatchedRank = diableRankCheck ? false : localProcedure.returntype.rank != inferedType.rank;
                    bool isReturnTypeMatch = (localProcedure.returntype.UID == inferedType.UID) && !notMatchedRank;
                    if (!isReturnTypeMatch)
                    {
                        if (inferedType.UID != (int)PrimitiveType.kTypeVar && (!typeNode.ConvertibleTo(localProcedure.returntype.UID) || notMatchedRank))
                        {
                            // Log a warning and force conversion to null by popping the result to the LX register and pushing a null

                            ProtoCore.DSASM.ClassNode returnTypeNode = core.classTable.list[localProcedure.returntype.UID];
                            if (notMatchedRank)
                            {
                                buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kMismatchReturnType, "Function '" + localProcedure.name + "' " + "expects return value to be of type " + returnTypeNode.name + " and of rank " + localProcedure.returntype.rank, core.CurrentDSFileName, node.line, node.col);
                            }
                            else
                            {
                                buildStatus.LogWarning(ProtoCore.BuildData.WarningID.kMismatchReturnType, "Function '" + localProcedure.name + "' " + "expects return value to be of type " + returnTypeNode.name, core.CurrentDSFileName, node.line, node.col);
                            }
                            EmitPushNull();
                        }
                        else if (localProcedure.returntype.UID < (int)PrimitiveType.kMaxPrimitives && inferedType.UID > (int)PrimitiveType.kTypeNull)
                        {
                            // EmitInstrConsole(ProtoCore.DSASM.kw.cast, localProcedure.returntype.UID.ToString());
                            // EmitConvert(localProcedure.returntype.UID, localProcedure.returntype.rank);
                        }
                    }
                }
#endif
                }
            }
            EmitReturnToRegister(node.line, node.col, node.endLine, node.endCol);
        }

        protected void EmitBinaryOperation(Type leftType, Type rightType, ProtoCore.DSASM.Operator optr)
        {
            EmitInstrConsole(ProtoCore.DSASM.kw.pop, ProtoCore.DSASM.kw.regBX);
            ProtoCore.DSASM.StackValue opBX = new ProtoCore.DSASM.StackValue();
            opBX.optype = ProtoCore.DSASM.AddressType.Register;
            opBX.opdata = (int)ProtoCore.DSASM.Registers.BX;
            EmitPop(opBX, Constants.kGlobalScope);

            EmitInstrConsole(ProtoCore.DSASM.kw.pop, ProtoCore.DSASM.kw.regAX);
            ProtoCore.DSASM.StackValue opAX = new ProtoCore.DSASM.StackValue();
            opAX.optype = ProtoCore.DSASM.AddressType.Register;
            opAX.opdata = (int)ProtoCore.DSASM.Registers.AX;
            EmitPop(opAX, Constants.kGlobalScope);

            // TODO Jun: double operations are executed in the main instruction for now, until this proves to be a performance issue
            bool isDoubleOp = false; // (optype1 == ProtoCore.DSASM.AddressType.Double || optype2 == ProtoCore.DSASM.AddressType.Double);

            optr = (isDoubleOp) ? opKwData.opDoubleTable[optr] : optr;
            string op = opKwData.opStringTable[optr];
            EmitInstrConsole(op, ProtoCore.DSASM.kw.regAX, ProtoCore.DSASM.kw.regBX);
            EmitBinary(opKwData.opCodeTable[optr], opAX, opBX);

            EmitInstrConsole(ProtoCore.DSASM.kw.push, ProtoCore.DSASM.kw.regAX);
            ProtoCore.DSASM.StackValue opRes = new ProtoCore.DSASM.StackValue();
            opRes.optype = ProtoCore.DSASM.AddressType.Register;
            opRes.opdata = (int)ProtoCore.DSASM.Registers.AX;
            EmitPush(opRes);
        }


        /*
            proc EmitIdentifierListNode(identListNode, graphnode)
	            // Build the dependency given the SSA form
	            BuildSSADependency(identListNode, graphnode) 

                // Build the dependency based on the non-SSA code
	            BuildRealDependencyForIdentList(identListNode, graphnode)
            end

            proc BuildSSADependency(identListNode, graphnode)
	            // This is the current implementation
            end


            proc BuildRealDependencyForIdentList(identListNode, graphnode)
	            dependent = new graphnode
	            dependent.Push(ssaPtrList.GetAll())
                dependent.Push(identListNode.rhs)
                graphnode.PushDependent(dependent)
            end
        */


        private void BuildSSADependency(Node node, AssociativeGraph.GraphNode graphNode)
        {
            // Jun Comment: set the graphNode dependent as this identifier list
            ProtoCore.Type type = new ProtoCore.Type();
            type.UID = globalClassIndex;
            ProtoCore.AssociativeGraph.UpdateNodeRef nodeRef = new AssociativeGraph.UpdateNodeRef();
            DFSGetSymbolList(node, ref type, nodeRef);

            if (null != graphNode && nodeRef.nodeList.Count > 0)
            {
                ProtoCore.AssociativeGraph.GraphNode dependentNode = new ProtoCore.AssociativeGraph.GraphNode();
                dependentNode.updateNodeRefList.Add(nodeRef);
                graphNode.PushDependent(dependentNode);
            }
        }


        private ProtoCore.AST.AssociativeAST.IdentifierListNode BuildIdentifierList(List<ProtoCore.AST.AssociativeAST.AssociativeNode> astIdentList)
        {
            // TODO Jun: Replace this condition or handle this case prior to this call
            if (astIdentList.Count < 2)
            {
                return null;
            }

            AST.AssociativeAST.IdentifierListNode identList = null;

            // Build the first ident list
            identList = new AST.AssociativeAST.IdentifierListNode();
            identList.LeftNode = astIdentList[0];
            identList.RightNode = astIdentList[1];

            // Build the rest
            for (int n = 2; n < astIdentList.Count; ++n)
            {
                // Build a new identllist for the prev identlist
                AST.AssociativeAST.IdentifierListNode subIdentList = new AST.AssociativeAST.IdentifierListNode(identList);
                subIdentList.Optr = Operator.dot;

                // Build a new ident and assign it the prev identlist and the next identifier
                identList = new AST.AssociativeAST.IdentifierListNode();
                identList.LeftNode = subIdentList;
                identList.RightNode = astIdentList[n];
            }

            return identList;
        }

        protected void BuildRealDependencyForIdentList(AssociativeGraph.GraphNode graphNode)
        {
	        AssociativeGraph.GraphNode dependent = new AssociativeGraph.GraphNode();

            // Push all dependent pointers
            ProtoCore.AST.AssociativeAST.IdentifierListNode identList = BuildIdentifierList(ssaPointerList);

            // Comment Jun: perhaps this can be an assert?
            if (null != identList)
            {
                ProtoCore.Type type = new ProtoCore.Type();
                type.UID = globalClassIndex;
                ProtoCore.AssociativeGraph.UpdateNodeRef nodeRef = new AssociativeGraph.UpdateNodeRef();
                DFSGetSymbolList(identList, ref type, nodeRef);

                if (null != graphNode && nodeRef.nodeList.Count > 0)
                {
                    ProtoCore.AssociativeGraph.GraphNode dependentNode = new ProtoCore.AssociativeGraph.GraphNode();
                    dependentNode.updateNodeRefList.Add(nodeRef);
                    graphNode.PushDependent(dependentNode);
                }
            }
        }

        protected void EmitIdentifierListNode(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.AssociativeGraph.GraphNode graphNode = null, ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone, ProtoCore.AST.Node bnode = null)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                //to process all unbounded parameters if any
                dynamic iNode = node;
                while (iNode is ProtoCore.AST.AssociativeAST.IdentifierListNode || iNode is ProtoCore.AST.ImperativeAST.IdentifierListNode)
                {
                    dynamic rightNode = iNode.RightNode;
                    if (rightNode is ProtoCore.AST.AssociativeAST.FunctionCallNode || rightNode is ProtoCore.AST.ImperativeAST.FunctionCallNode)
                    {
                        foreach (dynamic paramNode in rightNode.FormalArguments)
                        {
                            ProtoCore.Type paramType = new ProtoCore.Type();
                            paramType.UID = (int)ProtoCore.PrimitiveType.kTypeVoid;
                            paramType.IsIndexable = false;
                            DfsTraverse(paramNode, ref paramType, false, graphNode, DSASM.AssociativeSubCompilePass.kUnboundIdentifier);
                        }
                    }
                    iNode = iNode.LeftNode;
                }
                return;
            }

            int depth = 0;

            ProtoCore.Type leftType = new ProtoCore.Type();
            leftType.UID = (int)PrimitiveType.kInvalidType;
            leftType.IsIndexable = false;
            bool isFirstIdent = true;


            // Handle static calls to reflect the original call
            if (resolveStatic)
            {
                ProtoCore.AST.AssociativeAST.IdentifierListNode identList = node as ProtoCore.AST.AssociativeAST.IdentifierListNode;
                Validity.Assert(identList.LeftNode is ProtoCore.AST.AssociativeAST.IdentifierNode);
                Validity.Assert(!string.IsNullOrEmpty(staticClass));
                identList.LeftNode = new ProtoCore.AST.AssociativeAST.IdentifierNode(staticClass);

                staticClass = null;
                resolveStatic = false;

                ssaPointerList.Clear();
            }

            BuildSSADependency(node, graphNode);
            if (core.Options.FullSSA)
            {
                BuildRealDependencyForIdentList(graphNode);

                if (node is ProtoCore.AST.AssociativeAST.IdentifierListNode)
                {
                    if ((node as ProtoCore.AST.AssociativeAST.IdentifierListNode).isLastSSAIdentListFactor)
                    {
                        Validity.Assert(null != ssaPointerList);
                        ssaPointerList.Clear();
                    }
                }
            }

            bool isCollapsed;
            EmitGetterSetterForIdentList(node, ref inferedType, graphNode, subPass, out isCollapsed);
            if (!isCollapsed)
            {
                bool isMethodCallPresent = false;
                ProtoCore.DSASM.SymbolNode firstSymbol = null;
                bool isIdentReference = DfsEmitIdentList(node, null, globalClassIndex, ref leftType, ref depth, ref inferedType, false, ref isFirstIdent, ref isMethodCallPresent, ref firstSymbol, graphNode, subPass, bnode);
                inferedType.UID = isBooleanOp ? (int)PrimitiveType.kTypeBool : inferedType.UID;


                if (isIdentReference && depth > 1)
                {
                    EmitInstrConsole(ProtoCore.DSASM.kw.pushlist, depth.ToString(), globalClassIndex.ToString());

                    // TODO Jun: Get blockid
                    int blockId = 0;
                    EmitPushList(depth, globalClassIndex, blockId);
                }

                
#if PROPAGATE_PROPERTY_MODIFY_VIA_METHOD_UPDATE
                if (isMethodCallPresent)
                {
                    // Comment Jun: If the first symbol is null, it is a constructor. If you see it isnt, pls tell me
                    if (null != firstSymbol)
                    {
                        ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();

                        if (firstSymbol.classScope != ProtoCore.DSASM.Constants.kInvalidIndex &&
                            firstSymbol.functionIndex == ProtoCore.DSASM.Constants.kGlobalScope)
                        {
                            // Member var
                            op.optype = (firstSymbol.isStatic) ? ProtoCore.DSASM.AddressType.StaticMemVarIndex : ProtoCore.DSASM.AddressType.MemVarIndex;
                            op.opdata = firstSymbol.symbolTableIndex;
                        }
                        else
                        {
                            op.optype = ProtoCore.DSASM.AddressType.VarIndex;
                            op.opdata = firstSymbol.symbolTableIndex;
                        }

                        EmitPushDependencyData(currentBinaryExprUID, false);
                        EmitInstrConsole(ProtoCore.DSASM.kw.dep, firstSymbol.name);
                        EmitDependency(firstSymbol.runtimeTableIndex, op, 0);
                    }
                }
#endif
            }
        }

        protected void EmitDefaultArgNode(ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }
            EmitInstrConsole(ProtoCore.DSASM.kw.push, "defaultArg");
            EmitPush(StackUtils.BuildDefaultArgument());
        }

        /*
        protected void EmitPushVarData(int block, int dimensions)
        {
            // TODO Jun: Consider adding the block and dimension information in the instruction instead of storing them on the stack

            // Push the identifier block information 
            EmitInstrConsole(ProtoCore.DSASM.kw.push, block + "[block]");
            ProtoCore.DSASM.StackValue opblock = new ProtoCore.DSASM.StackValue();
            opblock.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            opblock.opdata = block;
            EmitPush(opblock);

            // TODO Jun: Performance 
            // Is it faster to have a 'pop' specific to arrays to prevent popping dimension for pop to instruction?
            EmitInstrConsole(ProtoCore.DSASM.kw.push, dimensions + "[dim]");
            ProtoCore.DSASM.StackValue opdim = new ProtoCore.DSASM.StackValue();
            opdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
            opdim.opdata = dimensions;
            EmitPush(opdim);

        }
        */
       
         
        protected void EmitPushVarData(int block, int dimensions, int UID = (int)ProtoCore.PrimitiveType.kTypeVar, int rank = 0)
        {
            // TODO Jun: Consider adding the block and dimension information in the instruction instead of storing them on the stack

            // Push the identifier block information 
            EmitInstrConsole(ProtoCore.DSASM.kw.push, block + "[block]");
            ProtoCore.DSASM.StackValue opblock = new ProtoCore.DSASM.StackValue();
            opblock.optype = ProtoCore.DSASM.AddressType.BlockIndex;
            opblock.opdata = block;
            EmitPush(opblock);

            // TODO Jun: Performance 
            // Is it faster to have a 'pop' specific to arrays to prevent popping dimension for pop to instruction?
            EmitInstrConsole(ProtoCore.DSASM.kw.push, dimensions + "[dim]");
            ProtoCore.DSASM.StackValue opdim = new ProtoCore.DSASM.StackValue();
            opdim.optype = ProtoCore.DSASM.AddressType.ArrayDim;
            opdim.opdata = dimensions;
            EmitPush(opdim);


            // Push the identifier block information 
            string srank = "";
            if (rank == Constants.nDimensionArrayRank)
            {
                srank = "[]..[]";
            }
            else
            {
                for (int i = 0; i < rank; ++i)
                {
                    srank += "[]";
                }
            }
            EmitInstrConsole(ProtoCore.DSASM.kw.push, UID + srank + "[type]");
            EmitPushType(UID, rank);
        }

        protected void EmitDepX()
        {
            ProtoCore.DSASM.Instruction instr = new ProtoCore.DSASM.Instruction();
            instr.opCode = ProtoCore.DSASM.OpCode.DEPX;
            EmitInstrConsole(ProtoCore.DSASM.kw.depx);

            ++pc;
            AppendInstruction(instr);
        }

        protected void EmitDynamicNode(ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == ProtoCore.DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }

            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.Dynamic;
            op.opdata = 0;
            op.opdata_d = 0.0;
            EmitInstrConsole(ProtoCore.DSASM.kw.push, "dynamic");
            EmitPush(op);
        }

        protected void EmitThisPointerNode(ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone)
        {
            if (subPass == ProtoCore.DSASM.AssociativeSubCompilePass.kUnboundIdentifier)
            {
                return;
            }
            ProtoCore.DSASM.StackValue op = new ProtoCore.DSASM.StackValue();
            op.optype = ProtoCore.DSASM.AddressType.ThisPtr;
            op.opdata = 0;
            op.opdata_d = 0.0;
            EmitInstrConsole(ProtoCore.DSASM.kw.push, "thisPtr");
            EmitPush(op);
        }

        protected List<ProtoCore.DSASM.AttributeEntry> PopulateAttributes(dynamic attributenodes)
        {
            List<ProtoCore.DSASM.AttributeEntry> attributes = new List<DSASM.AttributeEntry>();
            if (attributenodes == null)
                return attributes;
            Debug.Assert(attributenodes is List<ProtoCore.AST.AssociativeAST.AssociativeNode> || attributenodes is List<ProtoCore.AST.ImperativeAST.ImperativeNode>);
            foreach (dynamic anode in attributenodes)
            {
                ProtoCore.DSASM.AttributeEntry entry = PopulateAttribute(anode);
                if (entry != null)
                    attributes.Add(entry);
            }
            return attributes;
        }

        protected ProtoCore.DSASM.AttributeEntry PopulateAttribute(dynamic anode)
        {
            Debug.Assert(anode is ProtoCore.AST.AssociativeAST.FunctionCallNode || anode is ProtoCore.AST.ImperativeAST.FunctionCallNode);
            int cix = core.ClassTable.IndexOf(string.Format("{0}Attribute", anode.Function.Name));
            if (cix == ProtoCore.DSASM.Constants.kInvalidIndex)
            {
                buildStatus.LogSemanticError(string.Format("Unknown attribute {0}", anode.Function.Name), core.CurrentDSFileName, anode.line, anode.col);
            }
            ProtoCore.DSASM.AttributeEntry attribute = new ProtoCore.DSASM.AttributeEntry();
            attribute.ClassIndex = cix;
            attribute.Arguments = new List<Node>();
            foreach (dynamic attr in anode.FormalArguments)
            {
                if (!IsConstantExpression(attr))
                {
                    buildStatus.LogSemanticError("An attribute argument must be a constant expression", core.CurrentDSFileName, anode.line, anode.col);
                    return null;
                }
                attribute.Arguments.Add(attr as ProtoCore.AST.Node);
            }

            // TODO(Jiong): Do a check on the number of arguments 
            bool hasMatchedConstructor = false;
            foreach (ProtoCore.DSASM.ProcedureNode pn in core.ClassTable.ClassNodes[cix].vtable.procList)
            {
                if (pn.isConstructor && pn.argInfoList.Count == attribute.Arguments.Count)
                {
                    hasMatchedConstructor = true;
                    break;
                }
            }
            if (!hasMatchedConstructor)
            {
                buildStatus.LogSemanticError(string.Format("No constructors for Attribute '{0}' takes {1} arguments", anode.Function.Name, attribute.Arguments.Count), core.CurrentDSFileName, anode.line, anode.col);
                return null;
            }
            
            return attribute;
        }

        protected bool IsConstantExpression(ProtoCore.AST.Node node)
        {
            if (node is ProtoCore.AST.AssociativeAST.IntNode || 
                node is ProtoCore.AST.ImperativeAST.IntNode ||
                node is ProtoCore.AST.AssociativeAST.DoubleNode || 
                node is ProtoCore.AST.ImperativeAST.DoubleNode ||
                node is ProtoCore.AST.AssociativeAST.BooleanNode || 
                node is ProtoCore.AST.ImperativeAST.BooleanNode ||
                node is ProtoCore.AST.AssociativeAST.StringNode || 
                node is ProtoCore.AST.ImperativeAST.StringNode ||
                node is ProtoCore.AST.AssociativeAST.NullNode || 
                node is ProtoCore.AST.ImperativeAST.NullNode)
                return true;
            else if (node is ProtoCore.AST.AssociativeAST.BinaryExpressionNode)
            {
                ProtoCore.AST.AssociativeAST.BinaryExpressionNode bnode = node as ProtoCore.AST.AssociativeAST.BinaryExpressionNode;
                return IsConstantExpression(bnode.LeftNode) && IsConstantExpression(bnode.RightNode);
            }
            else if  (node is ProtoCore.AST.ImperativeAST.BinaryExpressionNode)
            {
                ProtoCore.AST.ImperativeAST.BinaryExpressionNode bnode = node as ProtoCore.AST.ImperativeAST.BinaryExpressionNode;
                return IsConstantExpression(bnode.RightNode) && IsConstantExpression(bnode.LeftNode);
            }
            else if (node is ProtoCore.AST.ImperativeAST.UnaryExpressionNode)
            {
                ProtoCore.AST.ImperativeAST.UnaryExpressionNode unode = node as ProtoCore.AST.ImperativeAST.UnaryExpressionNode;
                return IsConstantExpression(unode.Expression);
            }
            else if (node is ProtoCore.AST.AssociativeAST.UnaryExpressionNode)
            {
                ProtoCore.AST.AssociativeAST.UnaryExpressionNode unode = node as ProtoCore.AST.AssociativeAST.UnaryExpressionNode;
                return IsConstantExpression(unode.Expression);
            }
            else if (node is ProtoCore.AST.AssociativeAST.ExprListNode)
            {
                ProtoCore.AST.AssociativeAST.ExprListNode arraynode = node as ProtoCore.AST.AssociativeAST.ExprListNode;
                foreach (ProtoCore.AST.Node subnode in arraynode.list)
                {
                    if (!IsConstantExpression(subnode))
                        return false;
                }
                return true;
            }
            else if (node is ProtoCore.AST.ImperativeAST.ExprListNode)
            {
                ProtoCore.AST.ImperativeAST.ExprListNode arraynode = node as ProtoCore.AST.ImperativeAST.ExprListNode;
                foreach (ProtoCore.AST.Node subnode in arraynode.list)
                {
                    if (!IsConstantExpression(subnode))
                        return false;
                }
                return true;
            }
            else if (node is ProtoCore.AST.AssociativeAST.RangeExprNode)
            {
                ProtoCore.AST.AssociativeAST.RangeExprNode rangenode = node as ProtoCore.AST.AssociativeAST.RangeExprNode;
                return IsConstantExpression(rangenode.FromNode) && IsConstantExpression(rangenode.ToNode) && (rangenode.StepNode == null || IsConstantExpression(rangenode.StepNode));
            }
            else if (node is ProtoCore.AST.ImperativeAST.RangeExprNode)
            {
                ProtoCore.AST.ImperativeAST.RangeExprNode rangenode = node as ProtoCore.AST.ImperativeAST.RangeExprNode;
                return IsConstantExpression(rangenode.FromNode) && IsConstantExpression(rangenode.ToNode) && (rangenode.StepNode == null || IsConstantExpression(rangenode.StepNode));
            }

            return false;
        }
            
        protected bool InsideFunction()
        {
            ProtoCore.DSASM.CodeBlock cb = codeBlock;
            while (cb != null)
            {
                if (cb.blockType == ProtoCore.DSASM.CodeBlockType.kFunction)
                    return true;
                else if (cb.blockType == ProtoCore.DSASM.CodeBlockType.kLanguage)
                    return false;

                cb = cb.parent;
            }
            return false;
        }

        // used to manully emit "return = null" instruction if a function or language block does not have a return statement
        // there is update code involved in associativen code gen, so it is not implemented here
        protected abstract void EmitReturnNull();

        protected abstract void DfsTraverse(Node node, ref ProtoCore.Type inferedType, bool isBooleanOp = false, ProtoCore.AssociativeGraph.GraphNode graphNode = null, 
            ProtoCore.DSASM.AssociativeSubCompilePass subPass = ProtoCore.DSASM.AssociativeSubCompilePass.kNone, ProtoCore.AST.Node parentNode = null);
        
        protected static int staticPc;
        static int blk = 0;
        public static void setBlkId(int b){ blk = b; }
        public static int getBlkId() { return blk; }

        internal static void AuditCodeLocation(ProtoCore.Core core, ref string filePath, ref int line, ref int column)
        {
            // We don't attempt to change line and column numbers if 
            // they are already provided (caller can force update of 
            // them by setting either one of them to be -1).
            if (!string.IsNullOrEmpty(filePath))
            {
                if (-1 != line && (-1 != column))
                    return;
            }

            // As we create internal functions like %dotarg() and %dot() and
            // append them to the end of the script, it is possible that the 
            // location is in these functions so that the pc dictionary doesn't
            // contain pc key and return maximum line number + 1. 
            // 
            // Need to check if is in internal function or not, If it is, need
            // to go back the last stack frame to get the correct pc value
            int pc = Constants.kInvalidPC;
            int codeBlock = 0;
            if (core != null)
            {
                pc = core.CurrentExecutive.CurrentDSASMExec.PC;
                codeBlock = core.RunningBlock;

                if (String.IsNullOrEmpty(filePath))
                {
                    filePath = core.CurrentDSFileName;
                }
            }
            if(core.Options.IsDeltaExecution)
            {
                GetLocationByGraphNode(core, ref line, ref column);

                if(line == Constants.kInvalidIndex)
                    GetLocationByPC(core, pc, codeBlock, ref line, ref column);
            }
            else
                GetLocationByPC(core, pc, codeBlock, ref line, ref column);
            
        }

        private static void GetLocationByPC(ProtoCore.Core core, int pc, int blk, ref int line, ref int column)
        {
            //--------Dictionary Structure:--------
            //--------Name: codeToLocation---------
            //----------KEY: ----------------------
            //----------------mergedKey: ----------
            //-------------------|- blk -----------
            //-------------------|- pc ------------
            //----------VALUE: --------------------
            //----------------location: -----------
            //-------------------|- line ----------
            //-------------------|- col -----------

            //Zip those integers into 64-bit ulong
            ulong mergedKey = (((ulong)blk) << 32 | ((uint)pc));
            ulong location = (((ulong)line) << 32 | ((uint)column));

            if (core.codeToLocation.ContainsKey(mergedKey))
            {
                location = core.codeToLocation[mergedKey];
            }

            foreach (KeyValuePair<ulong, ulong> kv in core.codeToLocation)
            {
                //Conditions: within same blk && find the largest key which less than mergedKey we want to find
                if ((((int)(kv.Key >> 32)) == blk) && (kv.Key < mergedKey))
                {
                    location = kv.Value;
                }
            }
            //Unzip the location
            line = ((int)(location >> 32));
            column = ((int)(location & 0x00000000ffffffff));
        }

        private static void GetLocationByGraphNode(ProtoCore.Core core, ref int line, ref int col)
        {
            ulong location = (((ulong)line) << 32 | ((uint)col));

            foreach (var prop in core.InterpreterProps)
            {
                bool fileScope = false;
                if (prop.executingGraphNode == null)
                    continue;

                int startpc = prop.executingGraphNode.updateBlock.startpc;
                int endpc = prop.executingGraphNode.updateBlock.endpc;
                int block = prop.executingGraphNode.languageBlockId;

                // Determine if the current executing graph node is in an imported file scope
                // If so, continue searching in the outer graph nodes for the line and col in the outer-most context - pratapa
                
                for (int i = startpc; i <= endpc; ++i)
                {
                    var instruction = core.DSExecutable.instrStreamList[block].instrList[i];
                    if (instruction.debug != null)
                    {
                        if (instruction.debug.Location.StartInclusive.SourceLocation.FilePath != null)
                        {
                            fileScope = true;
                            break;
                        }
                        else
                        {
                            fileScope = false;
                            break;
                        }
                    }
                }
                if (fileScope)
                    continue;
                

                foreach (var kv in core.codeToLocation)
                {
                    if ((((int)(kv.Key >> 32)) == block) && (kv.Key >= (ulong)startpc && kv.Key <= (ulong)endpc))
                    {
                        location = kv.Value;
                        line = ((int)(location >> 32));
                        col = ((int)(location & 0x00000000ffffffff));
                        break;
                    }
                }
                if (line != -1)
                    break;
            }
            
        }

        //public void updatePcDictionary(ProtoCore.AST.Node node, int blk)
        public void updatePcDictionary(int line, int col)
        {
            //If the node is null, skip this update
            //if (node == null) 
              //  return;

            blk = codeBlock.codeBlockId;
            //if ((node.line > 0) && (node.col > 0))
            if ((line > 0) && (col > 0))
            {
                ulong mergedKey = (((ulong)blk) << 32 | ((uint)pc));
                //ulong location = (((ulong)node.line) << 32 | ((uint)node.col));
                ulong location = (((ulong)line) << 32 | ((uint)col));

                //ProtoCore.Utils.Validity.Assert(!codeToLocation.ContainsKey(mergedKey));
                if (core.codeToLocation.ContainsKey(mergedKey))
                {
                    core.codeToLocation.Remove(mergedKey);
                }
                
                core.codeToLocation.Add(mergedKey, location);
            }
        }

        protected bool IsInLanguageBlockDefinedInFunction()
        {
            return (localProcedure != null && localProcedure.runtimeIndex != codeBlock.codeBlockId);
        }
    }
}
