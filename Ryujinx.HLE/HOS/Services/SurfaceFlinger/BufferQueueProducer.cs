﻿using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    class BufferQueueProducer : IGraphicBufferProducer
    {
        public BufferQueueCore Core { get; private set; }

        private uint _stickyTransform;

        private uint         _nextCallbackTicket;
        private uint         _currentCallbackTicket;
        private uint         _callbackTicket;

        private readonly object _callbackLock = new object();

        public BufferQueueProducer(BufferQueueCore core)
        {
            Core = core;

            _stickyTransform         = 0;
            _callbackTicket          = 0;
            _nextCallbackTicket      = 0;
            _currentCallbackTicket   = 0;
        }

        public override Status RequestBuffer(int slot, out GraphicBuffer graphicBuffer)
        {
            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    graphicBuffer = default;

                    return Status.NoInit;
                }

                if (slot < 0 || slot >= Core.Slots.Length || !Core.IsOwnedByProducerLocked(slot))
                {
                    graphicBuffer = default;

                    return Status.BadValue;
                }

                Core.Slots[slot].RequestBufferCalled = true;

                graphicBuffer = Core.Slots[slot].GraphicBuffer;

                return Status.Success;
            }
        }

        public override Status SetBufferCount(int bufferCount)
        {
            IConsumerListener listener = null;

            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    return Status.NoInit;
                }

                if (bufferCount > BufferSlotArray.NumBufferSlots)
                {
                    return Status.BadValue;
                }

                for (int slot = 0; slot < Core.Slots.Length; slot++)
                {
                    if (Core.Slots[slot].BufferState != BufferState.Dequeued)
                    {
                        return Status.BadValue;
                    }
                }

                if (bufferCount == 0)
                {
                    Core.OverrideMaxBufferCount = 0;
                    Core.SignalDequeueEvent();

                    return Status.Success;
                }

                int minBufferSlots = Core.GetMinMaxBufferCountLocked(false);

                if (minBufferSlots < bufferCount)
                {
                    return Status.BadValue;
                }

                Core.FreeAllBuffersLocked();
                Core.OverrideMaxBufferCount = bufferCount;
                Core.SignalDequeueEvent();

                listener = Core.ConsumerListener;
            }

            listener?.onBuffersReleased();

            return Status.Success;
        }

        public override Status DequeueBuffer(out int slot, out AndroidFence fence, bool async, uint width, uint height, PixelFormat format, uint usage)
        {
            if ((width == 0 && height != 0) || (height == 0 && width != 0))
            {
                slot  = BufferSlotArray.InvalidBufferSlot;
                fence = AndroidFence.NoFence;

                return Status.BadValue;
            }

            Status returnFlags = Status.Success;

            bool attachedByConsumer = false;

            lock (Core.Lock)
            {
                if (format == PixelFormat.Unknown)
                {
                    format = Core.DefaultBufferFormat;
                }

                usage |= Core.ConsumerUsageBits;

                Status status = WaitForFreeSlotThenRelock(async, out slot, out returnFlags);

                if (status != Status.Success)
                {
                    slot  = BufferSlotArray.InvalidBufferSlot;
                    fence = AndroidFence.NoFence;

                    return status;
                }

                if (slot == BufferSlotArray.InvalidBufferSlot)
                {
                    slot  = BufferSlotArray.InvalidBufferSlot;
                    fence = AndroidFence.NoFence;

                    Logger.PrintError(LogClass.SurfaceFlinger, "No available buffer slots");
                    return Status.Busy;
                }

                attachedByConsumer = Core.Slots[slot].AttachedByConsumer;

                if (width == 0 || height == 0)
                {
                    width  = (uint)Core.DefaultWidth;
                    height = (uint)Core.DefaultHeight;
                }

                Core.Slots[slot].BufferState = BufferState.Dequeued;

                GraphicBuffer graphicBuffer = Core.Slots[slot].GraphicBuffer;

                if (!Core.Slots[slot].HasGraphicBuffer 
                    || graphicBuffer.Width != width 
                    || graphicBuffer.Height != height 
                    || graphicBuffer.Format != format
                    || (graphicBuffer.Usage & usage) != usage)
                {
                    // NOTE: On Regular Android, this should set Status.BufferNeedsReallocation, but as Nintendo only support preallocated buffer, it just return here.

                    string formatedError = $"Preallocated buffer mismatch - slot {slot}\n" +
                                           $"available: Width = {graphicBuffer.Width} Height = {graphicBuffer.Height} format = {graphicBuffer.Format} Usage = {graphicBuffer.Usage:x} " +
                                           $"requested: Width = {width} Height = {height} format = {format} Usage = {usage:x}";

                    Logger.PrintError(LogClass.SurfaceFlinger, formatedError);

                    slot  = BufferSlotArray.InvalidBufferSlot;
                    fence = AndroidFence.NoFence;

                    return Status.NoInit;
                }

                fence = Core.Slots[slot].Fence;

                Core.Slots[slot].Fence = AndroidFence.NoFence;

                Core.CheckSystemEventsLocked(Core.GetMaxBufferCountLocked(async));
            }

            if ((returnFlags & Status.BufferNeedsReallocation) == Status.BufferNeedsReallocation)
            {
                // NOTE: Should never happens, this is a leftover that Nintendo missed to remove..
                // NOTE: This is supposed to allocate a graphic buffer if BufferNeedsReallocation is set..
                throw new NotImplementedException();
            }

            if (attachedByConsumer)
            {
                returnFlags |= Status.BufferNeedsReallocation;
            }

            return returnFlags;
        }

        public override Status DetachBuffer(int slot)
        {
            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    return Status.NoInit;
                }

                if (slot < 0 || slot >= Core.Slots.Length || !Core.IsOwnedByProducerLocked(slot))
                {
                    return Status.BadValue;
                }

                if (!Core.Slots[slot].RequestBufferCalled)
                {
                    Logger.PrintError(LogClass.SurfaceFlinger, $"Slot {slot} was detached without requesting a buffer");

                    return Status.BadValue;
                }

                Core.FreeBufferLocked(slot);
                Core.SignalDequeueEvent();

                return Status.Success;
            }
        }

        public override Status DetachNextBuffer(out GraphicBuffer graphicBuffer, out AndroidFence fence)
        {
            lock (Core.Lock)
            {
                Core.WaitWhileAllocatingLocked();

                if (Core.IsAbandoned)
                {
                    graphicBuffer = default;
                    fence         = AndroidFence.NoFence;

                    return Status.NoInit;
                }

                int nextBufferSlot = BufferSlotArray.InvalidBufferSlot;

                for (int slot = 0; slot < Core.Slots.Length; slot++)
                {
                    if (Core.Slots[slot].BufferState == BufferState.Free && Core.Slots[slot].HasGraphicBuffer)
                    {
                        if (nextBufferSlot == BufferSlotArray.InvalidBufferSlot || Core.Slots[slot].FrameNumber < Core.Slots[nextBufferSlot].FrameNumber)
                        {
                            nextBufferSlot = slot;
                        }
                    }
                }

                if (nextBufferSlot == BufferSlotArray.InvalidBufferSlot)
                {
                    graphicBuffer = default;
                    fence         = AndroidFence.NoFence;

                    return Status.NoMemory;
                }

                graphicBuffer = Core.Slots[nextBufferSlot].GraphicBuffer;
                fence         = Core.Slots[nextBufferSlot].Fence;

                Core.FreeBufferLocked(nextBufferSlot);

                return Status.Success;
            }
        }

        public override Status AttachBuffer(out int slot, ref GraphicBuffer graphicBuffer)
        {
            lock (Core.Lock)
            {
                Status status = WaitForFreeSlotThenRelock(false, out slot, out Status returnFlags);

                if (status != Status.Success)
                {
                    return status;
                }

                if (slot == BufferSlotArray.InvalidBufferSlot)
                {
                    Logger.PrintError(LogClass.SurfaceFlinger, "No available buffer slots");
                    return Status.Busy;
                }

                Core.Slots[slot].GraphicBuffer       = graphicBuffer;
                Core.Slots[slot].HasGraphicBuffer    = true;
                Core.Slots[slot].BufferState         = BufferState.Dequeued;
                Core.Slots[slot].Fence               = AndroidFence.NoFence;
                Core.Slots[slot].RequestBufferCalled = true;

                return returnFlags;
            }
        }

        public override Status QueueBuffer(int slot, ref QueueBufferInput input, out QueueBufferOutput output)
        {
            output = default;

            switch (input.ScalingMode)
            {
                case NativeWindowScalingMode.Freeze:
                case NativeWindowScalingMode.ScaleToWindow:
                case NativeWindowScalingMode.ScaleCrop:
                case NativeWindowScalingMode.Unknown:
                case NativeWindowScalingMode.NoScaleCrop:
                    break;
                default:
                    return Status.BadValue;
            }

            BufferItem item = new BufferItem();

            IConsumerListener frameAvailableListener = null;
            IConsumerListener frameReplaceListener   = null;

            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    return Status.NoInit;
                }

                int maxBufferCount = Core.GetMaxBufferCountLocked(input.Async != 0);

                if (input.Async != 0 && Core.OverrideMaxBufferCount != 0 && Core.OverrideMaxBufferCount < maxBufferCount)
                {
                    return Status.BadValue;
                }

                if (slot < 0 || slot >= Core.Slots.Length || !Core.IsOwnedByProducerLocked(slot))
                {
                    return Status.BadValue;
                }

                if (!Core.Slots[slot].RequestBufferCalled)
                {
                    Logger.PrintError(LogClass.SurfaceFlinger, $"Slot {slot} was queued without requesting a buffer");
                    return Status.BadValue;
                }

                input.Crop.Intersect(Core.Slots[slot].GraphicBuffer.ToRect(), out Rect croppedRect);

                if (croppedRect != input.Crop)
                {
                    return Status.BadValue;
                }

                Core.Slots[slot].Fence       = input.Fence;
                Core.Slots[slot].BufferState = BufferState.Queued;
                Core.FrameCounter++;
                Core.Slots[slot].FrameNumber = Core.FrameCounter;

                item.AcquireCalled             = Core.Slots[slot].AcquireCalled;
                item.GraphicBuffer             = Core.Slots[slot].GraphicBuffer;
                item.HasGraphicBuffer          = true;
                item.Crop                      = input.Crop;
                item.Transform                 = input.Transform;
                item.TransformToDisplayInverse = (input.Transform & NativeWindowTransform.InverseDisplay) == NativeWindowTransform.InverseDisplay;
                item.ScalingMode               = input.ScalingMode;
                item.Timestamp                 = input.Timestamp;
                item.IsAutoTimestamp           = input.IsAutoTimestamp != 0;
                item.SwapInterval              = input.SwapInterval;
                item.FrameNumber               = Core.FrameCounter;
                item.Slot                      = slot;
                item.Fence                     = input.Fence;
                item.IsDroppable               = Core.DequeueBufferCannotBlock || input.Async != 0;

                _stickyTransform = input.StickyTransform;

                if (Core.Queue.Count == 0)
                {
                    Core.Queue.Add(item);

                    frameAvailableListener = Core.ConsumerListener;
                }
                else
                {
                    BufferItem frontItem = Core.Queue[0];

                    if (frontItem.IsDroppable)
                    {
                        if (Core.StillTracking(ref frontItem))
                        {
                            Core.Slots[slot].BufferState = BufferState.Free;
                            Core.Slots[slot].FrameNumber = 0;
                        }

                        Core.Queue.RemoveAt(0);
                        Core.Queue.Insert(0, item);

                        frameReplaceListener = Core.ConsumerListener;
                    }
                    else
                    {
                        Core.Queue.Add(item);

                        frameAvailableListener = Core.ConsumerListener;
                    }
                }

                Core.BufferHasBeenQueued = true;
                Core.SignalDequeueEvent();

                Core.CheckSystemEventsLocked(maxBufferCount);

                output = new QueueBufferOutput
                {
                    Width             = (uint)Core.DefaultWidth,
                    Height            = (uint)Core.DefaultHeight,
                    TransformHint     = Core.TransformHint,
                    NumPendingBuffers = (uint)Core.Queue.Count
                };

                _callbackTicket = _nextCallbackTicket++;
            }

            lock (_callbackLock)
            {
                while (_callbackTicket != _currentCallbackTicket)
                {
                    Monitor.Wait(_callbackLock);
                }

                frameAvailableListener?.OnFrameAvailable(ref item);
                frameReplaceListener?.OnFrameReplaced(ref item);

                _currentCallbackTicket++;

                Monitor.PulseAll(_callbackLock);
            }

            return Status.Success;
        }

        public override void CancelBuffer(int slot, ref AndroidFence fence)
        {
            lock (Core.Lock)
            {
                if (Core.IsAbandoned || slot < 0 || slot >= Core.Slots.Length || !Core.IsOwnedByProducerLocked(slot))
                {
                    return;
                }

                Core.Slots[slot].BufferState = BufferState.Free;
                Core.Slots[slot].FrameNumber = 0;
                Core.Slots[slot].Fence       = fence;
                Core.SignalDequeueEvent();
            }
        }

        public override Status Query(NativeWindowAttribute what, out int outValue)
        {
            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    outValue = 0;
                    return Status.NoInit;
                }

                switch (what)
                {
                    case NativeWindowAttribute.Width:
                        outValue = Core.DefaultWidth;
                        return Status.Success;
                    case NativeWindowAttribute.Height:
                        outValue = Core.DefaultHeight;
                        return Status.Success;
                    case NativeWindowAttribute.Format:
                        outValue = (int)Core.DefaultBufferFormat;
                        return Status.Success;
                    case NativeWindowAttribute.MinUnQueuedBuffers:
                        outValue = Core.GetMinUndequeuedBufferCountLocked(false);
                        return Status.Success;
                    case NativeWindowAttribute.ConsumerUsageBits:
                        outValue = (int)Core.ConsumerUsageBits;
                        return Status.Success;
                    case NativeWindowAttribute.MaxBufferCountAsync:
                        outValue = Core.GetMaxBufferCountLocked(true);
                        return Status.Success;
                    default:
                        outValue = 0;
                        return Status.BadValue;
                }
            }
        }

        public override Status Connect(IProducerListener listener, NativeWindowApi api, bool producerControlledByApp, out QueueBufferOutput output)
        {
            output = new QueueBufferOutput();

            lock (Core.Lock)
            {
                if (Core.IsAbandoned || Core.ConsumerListener == null)
                {
                    return Status.NoInit;
                }

                if (Core.ConnectedApi != NativeWindowApi.NoApi)
                {
                    return Status.BadValue;
                }

                Core.BufferHasBeenQueued      = false;
                Core.DequeueBufferCannotBlock = Core.ConsumerControlledByApp && producerControlledByApp;

                switch (api)
                {
                    case NativeWindowApi.NVN:
                    case NativeWindowApi.CPU:
                    case NativeWindowApi.Media:
                    case NativeWindowApi.Camera:
                        Core.ProducerListener = listener;
                        Core.ConnectedApi      = api;

                        output.Width             = (uint)Core.DefaultWidth;
                        output.Height            = (uint)Core.DefaultHeight;
                        output.TransformHint     = Core.TransformHint;
                        output.NumPendingBuffers = (uint)Core.Queue.Count;
                        return Status.Success;
                    default:
                        return Status.BadValue;
                }
            }
        }

        public override Status Disconnect(NativeWindowApi api)
        {
            IProducerListener producerListener = null;

            Status status = Status.BadValue;

            lock (Core.Lock)
            {
                if (Core.IsAbandoned)
                {
                    return Status.Success;
                }

                switch (api)
                {
                    case NativeWindowApi.NVN:
                    case NativeWindowApi.CPU:
                    case NativeWindowApi.Media:
                    case NativeWindowApi.Camera:
                        if (Core.ConnectedApi == api)
                        {
                            Core.FreeAllBuffersLocked();

                            producerListener = Core.ProducerListener;

                            Core.ProducerListener = null;
                            Core.ConnectedApi     = NativeWindowApi.NoApi;

                            Core.SignalDequeueEvent();

                            status = Status.Success;
                        }
                        break;
                }
            }

            producerListener?.onBufferReleased();

            return status;
        }

        public override Status SetPreallocatedBuffer(int slot, bool hasGraphicBuffer, ref GraphicBuffer graphicBuffer)
        {
            if (slot < 0 || slot >= Core.Slots.Length)
            {
                return Status.BadValue;
            }

            lock (Core.Lock)
            {
                Core.Slots[slot].BufferState           = BufferState.Free;
                Core.Slots[slot].GraphicBuffer         = graphicBuffer;
                Core.Slots[slot].HasGraphicBuffer      = hasGraphicBuffer;
                Core.Slots[slot].Fence                 = AndroidFence.NoFence;
                Core.Slots[slot].RequestBufferCalled   = false;
                Core.Slots[slot].NeedsCleanupOnRelease = false;
                Core.Slots[slot].FrameNumber           = 0;

                bool cleared = false;

                if (hasGraphicBuffer)
                {
                    // NOTE: Nintendo set the default width, height and format from the GraphicBuffer.. This is entirely wrong and should only be controlled by the consumer...
                    Core.DefaultWidth        = graphicBuffer.Width;
                    Core.DefaultHeight       = graphicBuffer.Height;
                    Core.DefaultBufferFormat = graphicBuffer.Format;
                }
                else
                {
                    foreach (BufferItem item in Core.Queue)
                    {
                        if (item.Slot >= BufferSlotArray.NumBufferSlots)
                        {
                            Core.Queue.Clear();
                            Core.FreeAllBuffersLocked();
                            Core.SignalDequeueEvent();
                            Core.SignalWaitBufferFreeEvent();
                            Core.SignalFrameAvailaibleEvent();

                            cleared = true;

                            break;
                        }
                    }
                }

                // The dequeue event must not be signaled two time in case of clean up but for some reasons, it still signal the wait buffer free event two time...
                if (!cleared)
                {
                    Core.SignalDequeueEvent();
                }

                Core.SignalWaitBufferFreeEvent();

                return Status.Success;
            }
        }

        private Status WaitForFreeSlotThenRelock(bool async, out int freeSlot, out Status returnStatus)
        {
            bool tryAgain = true;

            freeSlot     = BufferSlotArray.InvalidBufferSlot;
            returnStatus = Status.Success;

            while (tryAgain)
            {
                if (Core.IsAbandoned)
                {
                    freeSlot = BufferSlotArray.InvalidBufferSlot;

                    return Status.NoInit;
                }

                int maxBufferCount = Core.GetMaxBufferCountLocked(async);

                if (async && Core.OverrideMaxBufferCount != 0 && Core.OverrideMaxBufferCount < maxBufferCount)
                {
                    freeSlot = BufferSlotArray.InvalidBufferSlot;

                    return Status.BadValue;
                }

                for (int slot = maxBufferCount; slot < Core.Slots.Length; slot++)
                {
                    if (Core.Slots[slot].HasGraphicBuffer)
                    {
                        Core.FreeBufferLocked(slot);
                        returnStatus |= Status.ReleaseAllBuffers;
                    }
                }

                freeSlot = BufferSlotArray.InvalidBufferSlot;

                int dequeuedCount = 0;
                int acquiredCount = 0;

                for (int slot = 0; slot < maxBufferCount; slot++)
                {
                    switch (Core.Slots[slot].BufferState)
                    {
                        case BufferState.Acquired:
                            acquiredCount++;
                            break;
                        case BufferState.Dequeued:
                            dequeuedCount++;
                            break;
                        case BufferState.Free:
                            if (freeSlot == BufferSlotArray.InvalidBufferSlot || Core.Slots[slot].FrameNumber < Core.Slots[freeSlot].FrameNumber)
                            {
                                freeSlot = slot;
                            }
                            break;
                        default:
                            break;
                    }
                }

                // The producer SHOULD call SetBufferCount otherwise it's not allowed to dequeue multiple buffers.
                if (Core.OverrideMaxBufferCount == 0 && dequeuedCount > 0)
                {
                    return Status.InvalidOperation;
                }

                if (Core.BufferHasBeenQueued)
                {
                    int newUndequeuedCount = maxBufferCount - (dequeuedCount + 1);
                    int minUndequeuedCount = Core.GetMinUndequeuedBufferCountLocked(async);

                    if (newUndequeuedCount < minUndequeuedCount)
                    {
                        Logger.PrintError(LogClass.SurfaceFlinger, $"Min undequeued buffer count ({minUndequeuedCount}) exceeded (dequeued = {dequeuedCount} undequeued = {newUndequeuedCount})");

                        return Status.InvalidOperation;
                    }
                }

                bool tooManyBuffers = Core.Queue.Count > maxBufferCount;

                tryAgain = freeSlot == BufferSlotArray.InvalidBufferSlot || tooManyBuffers;

                if (tryAgain)
                {
                    if (async || (Core.DequeueBufferCannotBlock && acquiredCount < Core.MaxAcquiredBufferCount))
                    {
                        Core.CheckSystemEventsLocked(maxBufferCount);
                        return Status.WouldBlock;
                    }

                    Core.WaitDequeueEvent();
                }
            }

            return Status.Success;
        }

        protected override KReadableEvent GetWaitBufferFreeEvent()
        {
            return Core.GetWaitBufferFreeEvent();
        }
    }
}