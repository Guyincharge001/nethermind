﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class VirtualMachine : IVirtualMachine
    {
        public const int MaxCallDepth = 1024;
        public const int MaxStackSize = 1025;

        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly BigInteger P256Int = P255Int * 2;
        private static readonly BigInteger P255 = P255Int;
        private static readonly BigInteger BigInt256 = 256;
        public static readonly BigInteger BigInt32 = 32;
        public static readonly BigInteger BigIntMaxInt = int.MaxValue;
        private static readonly byte[] EmptyBytes = new byte[0];
        private static readonly byte[] BytesOne32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        };
        private static readonly byte[] BytesZero = {0};
        private static readonly byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };
        
        private readonly IBlockhashProvider _blockhashProvider;
        private readonly LruCache<Keccak, CodeInfo> _codeCache = new LruCache<Keccak, CodeInfo>(4 * 1024);
        private readonly ILogger _logger;
        private readonly IStateProvider _state;
        private readonly Stack<EvmState> _stateStack = new Stack<EvmState>();
        private readonly IStorageProvider _storage;
        private Address _parityTouchBugAccount;
        private Dictionary<BigInteger, IPrecompiledContract> _precompiles;
        private byte[] _returnDataBuffer = EmptyBytes;
        private TransactionTrace _trace;
        private TransactionTraceEntry _traceEntry;

        public VirtualMachine(IStateProvider stateProvider, IStorageProvider storageProvider, IBlockhashProvider blockhashProvider, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _state = stateProvider;
            _storage = storageProvider;
            _blockhashProvider = blockhashProvider;

            InitializePrecompiledContracts();
        }

        // can refactor and integrate the other call
        public (byte[] output, TransactionSubstate) Run(EvmState state, IReleaseSpec releaseSpec, TransactionTrace trace)
        {
            _traceEntry = null;
            _trace = trace;

            IReleaseSpec spec = releaseSpec;
            EvmState currentState = state;
            byte[] previousCallResult = null;
            byte[] previousCallOutput = Bytes.Empty;
            BigInteger previousCallOutputDestination = BigInteger.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = Bytes.Empty;
                }

                try
                {
                    if (_logger.IsDebugEnabled)
                    {
                        string intro = (currentState.IsContinuation ? "CONTINUE" : "BEGIN") + (currentState.IsStatic ? " STATIC" : string.Empty);
                        _logger.Debug($"{intro} {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} (at {currentState.Env.ExecutingAccount})");
                    }

                    CallResult callResult;
                    if (currentState.ExecutionType == ExecutionType.Precompile || currentState.ExecutionType == ExecutionType.DirectPrecompile)
                    {
                        callResult = ExecutePrecompile(currentState, spec);
                        if (!callResult.PrecompileSuccess.Value)
                        {
                            if (currentState.ExecutionType == ExecutionType.DirectPrecompile)
                            {
                                Metrics.EvmExceptions++;
                                // TODO: when direct / calls are treated same we should not need such differentiation
                                throw new PrecompileExecutionFailureException();
                            }

                            // TODO: testing it as it seems the way to pass zkSNARKs tests
                            currentState.GasAvailable = 0;
                        }
                    }
                    else
                    {
                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                            previousCallOutput = Bytes.Empty; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            //if (_logger.IsDebugEnabled)
                            //{
                            //    _logger.Debug($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");
                            //}

                            _state.Restore(currentState.StateSnapshot);
                            _storage.Restore(currentState.StorageSnapshot);

                            if (_parityTouchBugAccount != null)
                            {
                                _state.UpdateBalance(_parityTouchBugAccount, BigInteger.Zero, spec);
                                _parityTouchBugAccount = null;
                            }

                            if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectPrecompile || currentState.ExecutionType == ExecutionType.DirectCreate)
                            {
                                throw new EvmException();
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutput = Bytes.Empty;
                            previousCallOutputDestination = BigInteger.Zero;
                            _returnDataBuffer = Bytes.Empty;

                            currentState.Dispose();
                            currentState = _stateStack.Pop();
                            currentState.IsContinuation = true;
                            continue;
                        }
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectCreate || currentState.ExecutionType == ExecutionType.DirectPrecompile)
                    {
                        // TODO: review refund logic as there was a quick change for Refunds for Ropsten 2005537
                        return (callResult.Output, new TransactionSubstate(currentState.Refund, currentState.DestroyList, currentState.Logs, callResult.ShouldRevert));
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;

                    if (!callResult.ShouldRevert)
                    {
                        currentState.Refund += previousState.Refund;

                        foreach (Address address in previousState.DestroyList)
                        {
                            currentState.DestroyList.Add(address);
                        }

                        for (int i = 0; i < previousState.Logs.Count; i++)
                        {
                            LogEntry logEntry = previousState.Logs[i];
                            currentState.Logs.Add(logEntry);
                        }

                        long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                        if (previousState.ExecutionType == ExecutionType.Create || previousState.ExecutionType == ExecutionType.DirectCreate)
                        {
                            long codeDepositGasCost = GasCostOf.CodeDeposit * callResult.Output.Length;
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Code deposit cost is {codeDepositGasCost} ({GasCostOf.CodeDeposit} * {callResult.Output.Length})");
                            }

                            if (gasAvailableForCodeDeposit >= codeDepositGasCost)
                            {
                                Keccak codeHash = _state.UpdateCode(callResult.Output);

                                _state.UpdateCodeHash(callCodeOwner, codeHash, spec);
                                previousCallResult = callCodeOwner.Hex;

                                currentState.GasAvailable -= codeDepositGasCost;
                            }
                            else
                            {
                                // TODO: out of gas - try to handle as everywhere else - test with 61362 (7933dd) on Ropsten - second contract creation
                                previousCallResult = BytesZero;
                                if (releaseSpec.IsEip2Enabled)
                                {
                                    currentState.GasAvailable -= gasAvailableForCodeDeposit;
                                    // TODO: there should be an OutOfGasException here and a proper reversal of the account creation (and value transfer and all state changes called in the CREATE call)
                                    // TODO: instead just adding the simplest way to fix 552387 on Ropsten
                                    _state.DeleteAccount(callCodeOwner);
                                }
                                else
                                {
                                    previousCallResult = callCodeOwner.Hex;
                                }
                            }

                            previousCallOutput = Bytes.Empty;
                            previousCallOutputDestination = BigInteger.Zero;
                            _returnDataBuffer = Bytes.Empty;
                        }
                        else
                        {
                            previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                            previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = previousState.OutputDestination;
                            _returnDataBuffer = callResult.Output;
                        }

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"END {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? Bytes.Empty, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                        }
                    }
                    else
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"REVERT {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? Bytes.Empty, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                        }

                        _state.Restore(previousState.StateSnapshot);
                        _storage.Restore(previousState.StorageSnapshot);
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = previousState.OutputDestination;
                        _returnDataBuffer = callResult.Output;
                    }

                    previousState.Dispose();
                }
                catch (Exception ex) when (ex is EvmException || ex is OverflowException)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");
                    }

                    _state.Restore(currentState.StateSnapshot);
                    _storage.Restore(currentState.StorageSnapshot);

                    if (_parityTouchBugAccount != null)
                    {
                        _state.UpdateBalance(_parityTouchBugAccount, BigInteger.Zero, spec);
                        _parityTouchBugAccount = null;
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectPrecompile || currentState.ExecutionType == ExecutionType.DirectCreate)
                    {
                        throw;
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutput = Bytes.Empty;
                    previousCallOutputDestination = BigInteger.Zero;
                    _returnDataBuffer = Bytes.Empty;

                    currentState.Dispose();
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        public CodeInfo GetCachedCodeInfo(Address codeSource)
        {
            Keccak codeHash = _state.GetCodeHash(codeSource);
            CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
            if (cachedCodeInfo == null)
            {
                byte[] code = _state.GetCode(codeHash);
                if (code == null)
                {
                    return null;
                }

                cachedCodeInfo = new CodeInfo(code);
                _codeCache.Set(codeHash, cachedCodeInfo);
            }

            return cachedCodeInfo;
        }

        private void InitializePrecompiledContracts()
        {
            _precompiles = new Dictionary<BigInteger, IPrecompiledContract>
            {
                [EcRecoverPrecompiledContract.Instance.Address] = EcRecoverPrecompiledContract.Instance,
                [Sha256PrecompiledContract.Instance.Address] = Sha256PrecompiledContract.Instance,
                [Ripemd160PrecompiledContract.Instance.Address] = Ripemd160PrecompiledContract.Instance,
                [IdentityPrecompiledContract.Instance.Address] = IdentityPrecompiledContract.Instance,
                [Bn128AddPrecompiledContract.Instance.Address] = Bn128AddPrecompiledContract.Instance,
                [Bn128MulPrecompiledContract.Instance.Address] = Bn128MulPrecompiledContract.Instance,
                [Bn128PairingPrecompiledContract.Instance.Address] = Bn128PairingPrecompiledContract.Instance,
                [ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance
            };
        }

        public bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  UPDATE GAS (-{gasCost})");
            }

            if (gasAvailable < gasCost)
            {
                Metrics.EvmExceptions++;
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        public void RefundGas(long refund, ref long gasAvailable)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  UPDATE GAS (+{refund})");
            }

            gasAvailable += refund;
        }

        public CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
        {
            byte[] callData = state.Env.InputData;
            BigInteger transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            BigInteger precompileId = state.Env.CodeInfo.PrecompileId;
            long baseGasCost = _precompiles[precompileId].BaseGasCost();
            long dataGasCost = _precompiles[precompileId].DataGasCost(callData);

            bool wasCreated = false;
            if (!_state.AccountExists(state.Env.ExecutingAccount))
            {
                wasCreated = true;
                _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
            }
            else
            {
                _state.UpdateBalance(state.Env.ExecutingAccount, transferValue, spec);
            }

            if (gasAvailable < dataGasCost + baseGasCost)
            {
                // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-161.md
                // An additional issue was found in Parity,
                // where the Parity client incorrectly failed
                // to revert empty account deletions in a more limited set of contexts
                // involving out-of-gas calls to precompiled contracts;
                // the new Geth behavior matches Parity’s,
                // and empty accounts will cease to be a source of concern in general
                // in about one week once the state clearing process finishes.

                if (!wasCreated && transferValue.IsZero && spec.IsEip158Enabled)
                {
                    _parityTouchBugAccount = state.Env.ExecutingAccount;
                }

                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            //if(!UpdateGas(baseGasCost, ref gasAvailable)) return CallResult.Exception;
            //if(!UpdateGas(dataGasCost, ref gasAvailable)) return CallResult.Exception;
            if (!UpdateGas(baseGasCost, ref gasAvailable))
            {
                throw new OutOfGasException();
            }

            if (!UpdateGas(dataGasCost, ref gasAvailable))
            {
                throw new OutOfGasException();
            }

            state.GasAvailable = gasAvailable;

            try
            {
                (byte[] output, bool success) = _precompiles[precompileId].Run(callData);
                CallResult callResult = new CallResult(output);
                callResult.PrecompileSuccess = success;
                return callResult;
            }
            catch (Exception ex)
            {
                CallResult callResult = new CallResult(EmptyBytes);
                callResult.PrecompileSuccess = false;
                return callResult;
            }
        }

        public CallResult ExecuteCall(EvmState evmState, byte[] previousCallResult, byte[] previousCallOutput, BigInteger previousCallOutputDestination, IReleaseSpec spec)
        {
            ExecutionEnvironment env = evmState.Env;
            if (!evmState.IsContinuation)
            {
                if (!_state.AccountExists(env.ExecutingAccount))
                {
                    _state.CreateAccount(env.ExecutingAccount, env.TransferValue);
                }
                else
                {
                    _state.UpdateBalance(env.ExecutingAccount, env.TransferValue, spec);
                }

                if ((evmState.ExecutionType == ExecutionType.Create || evmState.ExecutionType == ExecutionType.DirectCreate) && spec.IsEip158Enabled)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (evmState.Env.CodeInfo.MachineCode == null || evmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                return CallResult.Empty;
            }

            evmState.InitStacks();
            Span<byte> bytesOnStack = evmState.BytesOnStack.AsSpan();
            int stackHead = evmState.StackHead;
            long gasAvailable = evmState.GasAvailable;
            long programCounter = (long)evmState.ProgramCounter;
            Span<byte> code = env.CodeInfo.MachineCode.AsSpan();

            void UpdateCurrentState()
            {
                evmState.ProgramCounter = programCounter;
                evmState.GasAvailable = gasAvailable;
                evmState.StackHead = stackHead;
            }

            void StartInstructionTrace(Instruction instruction, Span<byte> stack)
            {
                if (_trace == null)
                {
                    return;
                }

                Dictionary<string, string> previousStorage = _traceEntry?.Storage;
                _traceEntry = new TransactionTraceEntry();
                _traceEntry.Depth = env.CallDepth;
                _traceEntry.Gas = gasAvailable;
                _traceEntry.Operation = Enum.GetName(typeof(Instruction), instruction);
                _traceEntry.Memory = evmState.Memory.GetTrace();
                _traceEntry.Pc = programCounter;
                _traceEntry.Stack = GetStackTrace(stack);
                if (previousStorage != null)
                {
                    foreach (KeyValuePair<string, string> storageEntry in previousStorage)
                    {
                        _traceEntry.Storage.Add(storageEntry.Key, storageEntry.Value);
                    }
                }
            }

            void EndInstructionTrace()
            {
                if (_trace != null)
                {
                    _traceEntry.GasCost = _traceEntry.Gas - gasAvailable;
                    _trace.Entries.Add(_traceEntry);
                }
            }

            void PushBytes(Span<byte> value, Span<byte> stack)
            {
                if (value.Length != 32)
                {
                    stack.Slice(stackHead * 32, 32 - value.Length).Clear();
                }

                value.CopyTo(stack.Slice(stackHead * 32 + (32 - value.Length), value.Length));
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushBytesRightPadded(Span<byte> value, int paddingLength, Span<byte> stack)
            {
                if (value.Length != 32)
                {
                    stack.Slice(stackHead * 32, 32).Clear();
                }

                value.CopyTo(stack.Slice(stackHead * 32 + paddingLength, value.Length));
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushByte(byte value, Span<byte> stack)
            {
                stack.Slice(stackHead * 32, 32).Clear();
                stack[stackHead * 32 + 31] = value;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushOne(Span<byte> stack)
            {
                stack.Slice(stackHead * 32, 32).Clear();
                stack[stackHead * 32 + 31] = 1;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }
            
            void PushZero(Span<byte> stack)
            {
                stack.Slice(stackHead * 32, 32).Clear();
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }
            
            void PushInt(BigInteger value, Span<byte> stack)
            {
                Span<byte> target = stack.Slice(stackHead * 32, 32);
                int bytesToWrite = value.GetByteCount(true);
                if (bytesToWrite != 32)
                {
                    target.Clear();
                    target = target.Slice(32 - bytesToWrite, bytesToWrite);
                }

                value.TryWriteBytes(target, out int bytesWritten, true, true);

                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PopLimbo()
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;
            }

            void Dup(int depth, Span<byte> stack)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stack.Slice((stackHead - depth) * 32, 32).CopyTo(stack.Slice(stackHead * 32, 32));
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            Span<byte> wordBuffer = new byte[32].AsSpan();

            void Swap(int depth, Span<byte> stack, Span<byte> buffer)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                Span<byte> bottomSpan = stack.Slice((stackHead - depth) * 32, 32);
                Span<byte> topSpan = stack.Slice((stackHead - 1) * 32, 32);
                
                bottomSpan.CopyTo(buffer);
                topSpan.CopyTo(bottomSpan);
                buffer.CopyTo(topSpan);
            }

            Span<byte> PopBytes(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                return stack.Slice(stackHead * 32, 32);
            }

            byte PopByte(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                return stack[stackHead * 32 + 31];
            }

            List<string> GetStackTrace(Span<byte> stack)
            {
                List<string> stackTrace = new List<string>();
                for (int i = 0; i < stackHead; i++)
                {
                    Span<byte> stackItem = stack.Slice(i * 32, 32);
                    stackTrace.Add(new Hex(stackItem.ToArray()));
                }

                return stackTrace;
            }

            BigInteger PopUInt(Span<byte> stack)
            {
                return PopBytes(stack).ToUnsignedBigInteger();
            }

            BigInteger PopInt(Span<byte> stack)
            {
                return PopBytes(stack).ToSignedBigInteger(32);
            }

            Address PopAddress(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                return new Address(stack.Slice(stackHead * 32 + 12, 20).ToArray());
            }

            void UpdateMemoryCost(BigInteger position, BigInteger length)
            {
                long memoryCost = evmState.Memory.CalculateMemoryCost(position, length);
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  MEMORY COST {memoryCost}");
                }

                if (!UpdateGas(memoryCost, ref gasAvailable))
                {
                    throw new OutOfGasException();
                }
            }

            if (previousCallResult != null)
            {
                PushBytes(previousCallResult, bytesOnStack);
            }

            if (previousCallOutput.Length > 0)
            {
                UpdateMemoryCost(previousCallOutputDestination, previousCallOutput.Length);
                evmState.Memory.Save(previousCallOutputDestination, previousCallOutput);
            }

            while (programCounter < code.Length)
            {
                Instruction instruction = (Instruction)code[(int)programCounter];
                if (_trace != null) // TODO: review local method and move them to separate classes where needed and better
                {
                    StartInstructionTrace(instruction, bytesOnStack);
                }

                programCounter++;

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{instruction} (0x{instruction:X})");
                }

                BigInteger bigReg;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        UpdateCurrentState();
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    case Instruction.ADD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        BigInteger res = a + b;
                        PushInt(res >= P256Int ? res - P256Int : res, bytesOnStack);
                        break;
                    }
                    case Instruction.MUL:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place with Karatsuba
                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        PushInt(BigInteger.Remainder(a * b, P256Int), bytesOnStack);
                        break;
                    }
                    case Instruction.SUB:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        BigInteger res = a - b;
                        if (res.Sign < 0)
                        {
                            res += P256Int;
                        }

                        PushInt(res, bytesOnStack);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushInt(BigInteger.Divide(a, b), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SDIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt(bytesOnStack);
                        BigInteger b = PopInt(bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else if (b == BigInteger.MinusOne && a == P255Int)
                        {
                            PushInt(P255, bytesOnStack);
                        }
                        else
                        {
                            PushBytes(BigInteger.Divide(a, b).ToBigEndianByteArray(32), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        PushInt(b.IsZero ? BigInteger.Zero : BigInteger.Remainder(a, b), bytesOnStack);
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt(bytesOnStack);
                        BigInteger b = PopInt(bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushBytes((a.Sign * BigInteger.Remainder(a.Abs(), b.Abs()))
                                .ToBigEndianByteArray(32), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        BigInteger mod = PopUInt(bytesOnStack);

                        if (mod.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushInt(BigInteger.Remainder(a + b, mod), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        BigInteger mod = PopUInt(bytesOnStack);

                        if (mod.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushInt(BigInteger.Remainder(a * b, mod), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.EXP:
                    {
                        if (!UpdateGas(GasCostOf.Exp, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger baseInt = PopUInt(bytesOnStack);
                        BigInteger exp = PopUInt(bytesOnStack);
                        if (exp > BigInteger.Zero)
                        {
                            int expSize = (int)BigInteger.Log(exp, 256);
                            BigInteger expSizeTest = BigInteger.Pow(BigInt256, expSize);
                            BigInteger expSizeTestInc = expSizeTest * BigInt256;
                            if (expSizeTest > exp)
                            {
                                expSize--;
                            }
                            else if (expSizeTestInc <= exp)
                            {
                                expSize++;
                            }

                            if (!UpdateGas((spec.IsEip160Enabled ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * (1L + expSize), ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }
                        else
                        {
                            PushOne(bytesOnStack);
                            break;
                        }

                        if (baseInt.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else if (baseInt.IsOne)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushInt(BigInteger.ModPow(baseInt, exp, P256Int), bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        if (a >= BigInt32)
                        {
                            break;
                        }

                        Span<byte> b = PopBytes(bytesOnStack);
                        BitArray bits1 = b.ToBigEndianBitArray256();
                        int bitPosition = Math.Max(0, 248 - 8 * (int)a);
                        bool isSet = bits1[bitPosition];
                        for (int i = 0; i < bitPosition; i++)
                        {
                            bits1[i] = isSet;
                        }

                        PushBytes(bits1.ToBytes(), bytesOnStack);
                        break;
                    }
                    case Instruction.LT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        if(a < b)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.GT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        BigInteger b = PopUInt(bytesOnStack);
                        if(a > b)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.SLT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt(bytesOnStack);
                        BigInteger b = PopInt(bytesOnStack);
                        
                        if(BigInteger.Compare(a, b) < 0)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.SGT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt(bytesOnStack);
                        BigInteger b = PopInt(bytesOnStack);
                        if(BigInteger.Compare(a, b) > 0)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.EQ:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
                        if(a.SequenceEqual(b))
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        if (a.SequenceEqual(BytesZero32))
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }
                        break;
                    }
                    case Instruction.AND:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
                        for (int i = 0; i < 32; i++)
                        {
                            wordBuffer[i] = (byte)(a[i] & b[i]);
                        }

                        PushBytes(wordBuffer, bytesOnStack);
                        break;
                    }
                    case Instruction.OR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
                        for (int i = 0; i < 32; i++)
                        {
                            wordBuffer[i] = (byte)(a[i] | b[i]);
                        }

                        PushBytes(wordBuffer, bytesOnStack);
                        break;
                    }
                    case Instruction.XOR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
                        for (int i = 0; i < 32; i++)
                        {
                            wordBuffer[i] = (byte)(a[i] ^ b[i]);
                        }

                        PushBytes(wordBuffer, bytesOnStack);
                        break;
                    }
                    case Instruction.NOT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> bytes = PopBytes(bytesOnStack);
                        for (int i = 0; i < 32; ++i)
                        {
                            bytes[i] = (byte)~bytes[i];
                        }

                        PushBytes(bytes, bytesOnStack);
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger position = PopUInt(bytesOnStack);
                        Span<byte> bytes = PopBytes(bytesOnStack);

                        if (position >= BigInt32)
                        {
                            PushZero(bytesOnStack);
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)position;
                        if (adjustedPosition < 0)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushByte(bytes[adjustedPosition], bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SHA3:
                    {
                        BigInteger memSrc = PopUInt(bytesOnStack);
                        BigInteger memLength = PopUInt(bytesOnStack);
                        if (!UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(memLength),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(memSrc, memLength);

                        Span<byte> memData = evmState.Memory.LoadSpan(memSrc, memLength);
                        PushBytes(Keccak.Compute(memData).Bytes, bytesOnStack);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes((byte[])env.ExecutingAccount.Hex, bytesOnStack);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.BalanceEip150 : GasCostOf.Balance, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress(bytesOnStack);
                        BigInteger balance = _state.GetBalance(address);
                        PushInt(balance, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes((byte[])env.Sender.Hex, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.Value, bytesOnStack);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes((byte[])env.Originator.Hex, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger src = PopUInt(bytesOnStack);
                        PushBytes(env.InputData.SliceWithZeroPadding(src, 32), bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.InputData.Length, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        BigInteger dest = PopUInt(bytesOnStack);
                        BigInteger src = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);

                        byte[] callDataSlice = env.InputData.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(code.Length, bytesOnStack);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        BigInteger dest = PopUInt(bytesOnStack);
                        BigInteger src = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);
                        byte[] callDataSlice = code.ToArray().SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.GasPrice, bytesOnStack);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.ExtCodeSizeEip150 : GasCostOf.ExtCodeSize, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress(bytesOnStack);
                        byte[] accountCode = GetCachedCodeInfo(address)?.MachineCode;
                        PushInt(accountCode?.Length ?? BigInteger.Zero, bytesOnStack);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress(bytesOnStack);
                        BigInteger dest = PopUInt(bytesOnStack);
                        BigInteger src = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);
                        if (!UpdateGas((spec.IsEip150Enabled ? GasCostOf.ExtCodeEip150 : GasCostOf.ExtCode) + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);
                        byte[] externalCode = GetCachedCodeInfo(address)?.MachineCode;
                        byte[] callDataSlice = externalCode.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.RETURNDATASIZE:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(_returnDataBuffer.Length, bytesOnStack);
                        break;
                    }
                    case Instruction.RETURNDATACOPY:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        BigInteger dest = PopUInt(bytesOnStack);
                        BigInteger src = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);

                        if (src + length > _returnDataBuffer.Length)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.AccessViolationException;
                        }

                        byte[] returnDataSlice = _returnDataBuffer.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, returnDataSlice);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        if (!UpdateGas(GasCostOf.BlockHash, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt(bytesOnStack);
                        PushBytes(_blockhashProvider.GetBlockhash(env.CurrentBlock, a)?.Bytes ?? BytesZero32, bytesOnStack);

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes((byte[])env.CurrentBlock.Beneficiary.Hex, bytesOnStack);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Difficulty, bytesOnStack);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Timestamp, bytesOnStack);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Number, bytesOnStack);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.GasLimit, bytesOnStack);
                        break;
                    }
                    case Instruction.POP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt(bytesOnStack);
                        UpdateMemoryCost(memPosition, BigInt32);
                        Span<byte> memData = evmState.Memory.LoadSpan(memPosition);
                        PushBytes(memData, bytesOnStack);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt(bytesOnStack);
                        Span<byte> data = PopBytes(bytesOnStack);
                        UpdateMemoryCost(memPosition, BigInt32);
                        evmState.Memory.SaveWord(memPosition, data);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt(bytesOnStack);
                        byte data = PopByte(bytesOnStack);
                        UpdateMemoryCost(memPosition, BigInteger.One);
                        evmState.Memory.SaveByte(memPosition, data);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.SLoadEip150 : GasCostOf.SLoad, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger storageIndex = PopUInt(bytesOnStack);
                        byte[] value = _storage.Get(new StorageAddress(env.ExecutingAccount, storageIndex));
                        PushBytes(value, bytesOnStack);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        // fail fast before the first storage read
                        if (!UpdateGas(GasCostOf.SReset, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger storageIndex = PopUInt(bytesOnStack);
                        byte[] data = PopBytes(bytesOnStack).ToArray().WithoutLeadingZeros();

                        bool isNewValueZero = data.IsZero();

                        StorageAddress storageAddress = new StorageAddress(env.ExecutingAccount, storageIndex);
                        byte[] previousValue = _storage.Get(storageAddress);

                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, data);


                        if (isNewValueZero)
                        {
                            // either case would be reset cost here first
                            if (isValueChanged)
                            {
                                evmState.Refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            // if not zero would be reset cost here
                            if (previousValue.IsZero() && !UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (isValueChanged)
                        {
                            byte[] newValue = isNewValueZero ? BytesZero : data;
                            _storage.Set(storageAddress, newValue);
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"  UPDATING STORAGE: {env.ExecutingAccount} {storageIndex} {Hex.FromBytes(newValue, true)}");
                            }
                        }

                        if (_trace != null)
                        {
                            _traceEntry.Storage[new Hex(storageIndex.ToBigEndianByteArray().PadLeft(32))] = new Hex(data.PadLeft(32));
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        bigReg = PopUInt(bytesOnStack);
                        if (bigReg > BigIntMaxInt)
                        {
                            Metrics.EvmExceptions++;
                            throw new InvalidJumpDestinationException();
                            return CallResult.InvalidJumpDestination;
                        }

                        int dest = (int)bigReg;
                        if (!env.CodeInfo.ValidateJump(dest)) return CallResult.InvalidJumpDestination;

                        programCounter = dest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        bigReg = PopUInt(bytesOnStack);
                        Span<byte> condition = PopBytes(bytesOnStack);
                        if (!condition.SequenceEqual(BytesZero32))
                        {
                            if (bigReg > BigIntMaxInt)
                            {
                                Metrics.EvmExceptions++;
                                throw new InvalidJumpDestinationException();
                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
                            }

                            int dest = (int)bigReg;

                            if (!env.CodeInfo.ValidateJump(dest))
                            {
                                throw new InvalidJumpDestinationException();
                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                            }

                            programCounter = dest;
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(programCounter - 1L, bytesOnStack);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(evmState.Memory.Size, bytesOnStack);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(gasAvailable, bytesOnStack);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        break;
                    }
                    case Instruction.PUSH1:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        int programCounterInt = (int)programCounter;
                        if (programCounterInt >= code.Length)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushByte(code[programCounterInt], bytesOnStack);
                        }

                        programCounter++;
                        break;
                    }
                    case Instruction.PUSH2:
                    case Instruction.PUSH3:
                    case Instruction.PUSH4:
                    case Instruction.PUSH5:
                    case Instruction.PUSH6:
                    case Instruction.PUSH7:
                    case Instruction.PUSH8:
                    case Instruction.PUSH9:
                    case Instruction.PUSH10:
                    case Instruction.PUSH11:
                    case Instruction.PUSH12:
                    case Instruction.PUSH13:
                    case Instruction.PUSH14:
                    case Instruction.PUSH15:
                    case Instruction.PUSH16:
                    case Instruction.PUSH17:
                    case Instruction.PUSH18:
                    case Instruction.PUSH19:
                    case Instruction.PUSH20:
                    case Instruction.PUSH21:
                    case Instruction.PUSH22:
                    case Instruction.PUSH23:
                    case Instruction.PUSH24:
                    case Instruction.PUSH25:
                    case Instruction.PUSH26:
                    case Instruction.PUSH27:
                    case Instruction.PUSH28:
                    case Instruction.PUSH29:
                    case Instruction.PUSH30:
                    case Instruction.PUSH31:
                    case Instruction.PUSH32:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        int length = instruction - Instruction.PUSH1 + 1;
                        int programCounterInt = (int)programCounter;
                        int usedFromCode = Math.Min(code.Length - programCounterInt, length);

                        PushBytes(code.ToArray().Slice(programCounterInt, usedFromCode).PadRight(length), bytesOnStack);
                        //PushBytesRightPadded(code.Slice(programCounterInt, usedFromCode), length, bytesOnStack);

                        programCounter += length;
                        break;
                    }
                    case Instruction.DUP1:
                    case Instruction.DUP2:
                    case Instruction.DUP3:
                    case Instruction.DUP4:
                    case Instruction.DUP5:
                    case Instruction.DUP6:
                    case Instruction.DUP7:
                    case Instruction.DUP8:
                    case Instruction.DUP9:
                    case Instruction.DUP10:
                    case Instruction.DUP11:
                    case Instruction.DUP12:
                    case Instruction.DUP13:
                    case Instruction.DUP14:
                    case Instruction.DUP15:
                    case Instruction.DUP16:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Dup(instruction - Instruction.DUP1 + 1, bytesOnStack);
                        break;
                    }
                    case Instruction.SWAP1:
                    case Instruction.SWAP2:
                    case Instruction.SWAP3:
                    case Instruction.SWAP4:
                    case Instruction.SWAP5:
                    case Instruction.SWAP6:
                    case Instruction.SWAP7:
                    case Instruction.SWAP8:
                    case Instruction.SWAP9:
                    case Instruction.SWAP10:
                    case Instruction.SWAP11:
                    case Instruction.SWAP12:
                    case Instruction.SWAP13:
                    case Instruction.SWAP14:
                    case Instruction.SWAP15:
                    case Instruction.SWAP16:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Swap(instruction - Instruction.SWAP1 + 2, bytesOnStack, wordBuffer);
                        break;
                    }
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        BigInteger memoryPos = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);
                        long topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(memoryPos, length);
                        if (!UpdateGas(
                            GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                            (long)length * GasCostOf.LogData, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        byte[] data = evmState.Memory.Load(memoryPos, length);
                        Keccak[] topics = new Keccak[topicsCount];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = new Keccak(PopBytes(bytesOnStack).ToArray());
                        }

                        LogEntry logEntry = new LogEntry(
                            env.ExecutingAccount,
                            data,
                            topics);
                        evmState.Logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_state.AccountExists(env.ExecutingAccount))
                        {
                            _state.CreateAccount(env.ExecutingAccount, BigInteger.Zero);
                        }

                        BigInteger value = PopUInt(bytesOnStack);
                        BigInteger memoryPositionOfInitCode = PopUInt(bytesOnStack);
                        BigInteger initCodeLength = PopUInt(bytesOnStack);

                        if (!UpdateGas(GasCostOf.Create, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(memoryPositionOfInitCode, initCodeLength);

                        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
                        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            // TODO: need a test for this
                            _returnDataBuffer = EmptyBytes;
                            PushZero(bytesOnStack);
                            break;
                        }

                        byte[] initCode = evmState.Memory.Load(memoryPositionOfInitCode, initCodeLength);
                        BigInteger balance = _state.GetBalance(env.ExecutingAccount);
                        if (value > _state.GetBalance(env.ExecutingAccount))
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Insufficient balance when calling create - value = {value} > {balance} = balance");
                            }

                            PushZero(bytesOnStack);
                            break;
                        }

                        long callGas = spec.IsEip150Enabled ? gasAvailable - gasAvailable / 64L : gasAvailable;
                        if (!UpdateGas(callGas, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Keccak contractAddressKeccak =
                            Keccak.Compute(
                                Rlp.Encode(
                                    Rlp.Encode(env.ExecutingAccount),
                                    Rlp.Encode(_state.GetNonce(env.ExecutingAccount))));
                        Address contractAddress = new Address(contractAddressKeccak);

                        _state.IncrementNonce(env.ExecutingAccount);

                        bool accountExists = _state.AccountExists(contractAddress);
                        if (accountExists && ((GetCachedCodeInfo(contractAddress)?.MachineCode?.Length ?? 0) != 0 || _state.GetNonce(contractAddress) != 0))
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Contract collision at {contractAddress}"); // the account already owns the contract with the code
                            }

                            PushZero(bytesOnStack); // TODO: this push 0 approach should be replaced with some proper approach to call result
                            break;
                        }

                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();

                        _state.UpdateBalance(env.ExecutingAccount, -value, spec);
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("  INIT: " + contractAddress);
                        }

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.TransferValue = value;
                        callEnv.Value = value;
                        callEnv.Sender = env.ExecutingAccount;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.ExecutingAccount = contractAddress;
                        callEnv.CodeInfo = new CodeInfo(initCode);
                        callEnv.InputData = Bytes.Empty;
                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            ExecutionType.Create,
                            stateSnapshot,
                            storageSnapshot,
                            0L,
                            0L,
                            evmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(callState);
                    }
                    case Instruction.RETURN:
                    {
                        BigInteger memoryPos = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);

                        UpdateMemoryCost(memoryPos, length);
                        byte[] returnData = evmState.Memory.Load(memoryPos, length);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(returnData);
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                    {
                        if (instruction == Instruction.DELEGATECALL && !spec.IsEip7Enabled ||
                            instruction == Instruction.STATICCALL && !spec.IsEip214Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        BigInteger gasLimit = PopUInt(bytesOnStack);
                        Address codeSource = PopAddress(bytesOnStack);
                        BigInteger callValue;
                        switch (instruction)
                        {
                            case Instruction.STATICCALL:
                                callValue = BigInteger.Zero;
                                break;
                            case Instruction.DELEGATECALL:
                                callValue = env.Value;
                                break;
                            default:
                                callValue = PopUInt(bytesOnStack);
                                break;
                        }

                        BigInteger transferValue = instruction == Instruction.DELEGATECALL ? BigInteger.Zero : callValue;
                        BigInteger dataOffset = PopUInt(bytesOnStack);
                        BigInteger dataLength = PopUInt(bytesOnStack);
                        BigInteger outputOffset = PopUInt(bytesOnStack);
                        BigInteger outputLength = PopUInt(bytesOnStack);

                        if (evmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        bool isPrecompile = codeSource.IsPrecompiled(spec);
                        Address sender = instruction == Instruction.DELEGATECALL ? env.Sender : env.ExecutingAccount;
                        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? codeSource : env.ExecutingAccount;

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  SENDER {sender}");
                            _logger.Debug($"  CODE SOURCE {codeSource}");
                            _logger.Debug($"  TARGET {target}");
                            _logger.Debug($"  VALUE {callValue}");
                            _logger.Debug($"  TRANSFER_VALUE {transferValue}");
                        }

                        long gasExtra = 0L;

                        if (!transferValue.IsZero)
                        {
                            gasExtra += GasCostOf.CallValue;
                        }

                        if (!spec.IsEip158Enabled && !_state.AccountExists(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (spec.IsEip158Enabled && transferValue != 0 && _state.IsDeadAccount(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.CallOrCallCodeEip150 : GasCostOf.CallOrCallCode, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dataOffset, dataLength);
                        UpdateMemoryCost(outputOffset, outputLength);
                        if (!UpdateGas(gasExtra, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        if (spec.IsEip150Enabled)
                        {
                            gasLimit = BigInteger.Min(gasAvailable - gasAvailable / 64L, gasLimit);
                        }

                        long gasLimitUl = (long)gasLimit;
                        if (!UpdateGas(gasLimitUl, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        if (!transferValue.IsZero)
                        {
                            gasLimitUl += GasCostOf.CallStipend;
                        }

                        if (env.CallDepth >= MaxCallDepth || !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
                        {
                            RefundGas(gasLimitUl, ref gasAvailable);
                            //evmState.Memory.Save(outputOffset, new byte[(int)outputLength]); // TODO: probably should not save memory here
                            _returnDataBuffer = EmptyBytes;
                            PushZero(bytesOnStack);
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug("  FAIL - CALL DEPTH");
                            }

                            break;
                        }

                        byte[] callData = evmState.Memory.Load(dataOffset, dataLength);
                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();
                        _state.UpdateBalance(sender, -transferValue, spec);

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.Originator = env.Originator;
                        callEnv.Sender = sender;
                        callEnv.ExecutingAccount = target;
                        callEnv.TransferValue = transferValue;
                        callEnv.Value = callValue;
                        callEnv.InputData = callData;
                        callEnv.CodeInfo = isPrecompile ? new CodeInfo(codeSource) : GetCachedCodeInfo(codeSource);

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  CALL_GAS {gasLimitUl}");
                        }

                        EvmState callState = new EvmState(
                            gasLimitUl,
                            callEnv,
                            isPrecompile ? ExecutionType.Precompile : (instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? ExecutionType.Call : ExecutionType.Callcode),
                            stateSnapshot,
                            storageSnapshot,
                            (long)outputOffset,
                            (long)outputLength,
                            instruction == Instruction.STATICCALL || evmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(callState);
                    }
                    case Instruction.REVERT:
                    {
                        if (!spec.IsEip140Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        BigInteger memoryPos = PopUInt(bytesOnStack);
                        BigInteger length = PopUInt(bytesOnStack);

                        UpdateMemoryCost(memoryPos, length);
                        byte[] errorDetails = evmState.Memory.Load(memoryPos, length);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(errorDetails, true);
                    }
                    case Instruction.INVALID:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        EndInstructionTrace();
                        Metrics.EvmExceptions++;
                        return CallResult.InvalidInstructionException;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        if (spec.IsEip150Enabled && !UpdateGas(GasCostOf.SelfDestructEip150, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Metrics.SelfDestructs++;

                        Address inheritor = PopAddress(bytesOnStack);
                        evmState.DestroyList.Add(env.ExecutingAccount);

                        BigInteger ownerBalance = _state.GetBalance(env.ExecutingAccount);
                        if (spec.IsEip158Enabled && ownerBalance != 0 && _state.IsDeadAccount(inheritor))
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        bool inheritorAccountExists = _state.AccountExists(inheritor);
                        if (!spec.IsEip158Enabled && !inheritorAccountExists && spec.IsEip150Enabled)
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (!inheritorAccountExists)
                        {
                            _state.CreateAccount(inheritor, ownerBalance);
                        }
                        else if (!inheritor.Equals(env.ExecutingAccount))
                        {
                            _state.UpdateBalance(inheritor, ownerBalance, spec);
                        }

                        _state.UpdateBalance(env.ExecutingAccount, -ownerBalance, spec);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    default:
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("UNKNOWN INSTRUCTION");
                        }

                        EndInstructionTrace();
                        Metrics.EvmExceptions++;
                        return CallResult.InvalidInstructionException;
                    }
                }

                EndInstructionTrace();
            }

            UpdateCurrentState();
            return CallResult.Empty;
        }
    }

    public class CallResult
    {
        public static CallResult Exception = new CallResult(StatusCode.FailureBytes) {IsException = true};
        public static CallResult OutOfGasException = Exception;
        public static CallResult AccessViolationException = Exception;
        public static CallResult InvalidJumpDestination = Exception;
        public static CallResult InvalidInstructionException = Exception;
        public static CallResult StaticCallViolationException = Exception;
        public static CallResult StackOverflowException = Exception; // TODO: use these to avoid CALL POP attacks
        public static CallResult StackUnderflowException = Exception; // TODO: use these to avoid CALL POP attacks
        public static readonly CallResult Empty = new CallResult();

        public CallResult(EvmState stateToExecute)
        {
            StateToExecute = stateToExecute;
        }

        private CallResult()
        {
        }

        public CallResult(byte[] output, bool shouldRevert = false)
        {
            ShouldRevert = shouldRevert;
            Output = output;
        }

        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; set; } // TODO: check this behaviour as it seems it is required and previously that was not the case

        public EvmState StateToExecute { get; }
        public byte[] Output { get; } = Bytes.Empty;
        public bool IsReturn => StateToExecute == null;
        public bool IsException { get; set; }
    }
}