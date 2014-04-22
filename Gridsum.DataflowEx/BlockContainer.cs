﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gridsum.DataflowEx.Exceptions;
using Gridsum.DataflowEx.PatternMatch;

namespace Gridsum.DataflowEx
{
    /// <summary>
    /// Core concept of DataflowEx. Represents a reusable dataflow component with its processing logic, which
    /// may contain one or multiple blocks. Inheritors should call RegisterBlock in their constructors.
    /// </summary>
    public abstract class BlockContainer : IBlockContainer
    {
        private static ConcurrentDictionary<string, IntHolder> s_nameDict = new ConcurrentDictionary<string, IntHolder>();
        protected readonly BlockContainerOptions m_containerOptions;
        protected readonly DataflowLinkOptions m_defaultLinkOption;
        protected Lazy<Task> m_completionTask;
        protected IList<BlockMeta> m_blockMetas = new List<BlockMeta>();
        protected string m_defaultName;

        protected class BlockMeta
        {
            public IDataflowBlock Block { get; set; }
            public Func<int> CountGetter { get; set; }
            public Task CompletionTask { get; set; }
        }

        public BlockContainer(BlockContainerOptions containerOptions)
        {
            m_containerOptions = containerOptions;
            m_defaultLinkOption = new DataflowLinkOptions() { PropagateCompletion = true };
            m_completionTask = new Lazy<Task>(GetCompletionTask, LazyThreadSafetyMode.ExecutionAndPublication);

            string friendlyName = Utils.GetFriendlyName(this.GetType());
            int count = s_nameDict.GetOrAdd(friendlyName, new IntHolder()).Increment();
            m_defaultName = friendlyName + count;
            
            if (m_containerOptions.ContainerMonitorEnabled || m_containerOptions.BlockMonitorEnabled)
            {
                StartPerformanceMonitorAsync();
            }
        }

        /// <summary>
        /// Display name of the container
        /// </summary>
        public virtual string Name
        {
            get { return m_defaultName; }
        }
        
        /// <summary>
        /// Register this block to block meta. Also make sure the container will fail if the registered block fails.
        /// </summary>
        protected void RegisterBlock(IDataflowBlock block, Func<int> countGetter = null, Action<Task> blockCompletionCallback = null)
        {
            if (block == null)
            {
                throw new ArgumentNullException("block");
            }

            if (m_completionTask.IsValueCreated)
            {
                throw new InvalidOperationException("You cannot register block after completion task has been generated. Please ensure you are calling RegisterBlock() inside constructor.");
            }

            if (m_blockMetas.Any(m => m.Block.Equals(block)))
            {
                throw new ArgumentException("Duplicate block registered in " + this.Name);
            }

            var tcs = new TaskCompletionSource<object>();
            
            block.Completion.ContinueWith(task =>
            {
                Exception originalException = null;
                try
                {
                    if (task.Status == TaskStatus.Faulted)
                    {
                        task.Exception.Flatten().Handle(e =>
                        {
                            if (e is PropagatedException)
                            {
                                //do nothing if e is not an orignal exception
                            }
                            else
                            {
                                if (originalException == null) originalException = e;
                            }
                            return true;
                        });
                    }
                   
                    //call callback
                    if (blockCompletionCallback != null)
                    {
                        blockCompletionCallback(task);
                    }
                }
                catch (Exception e)
                {
                    LogHelper.Logger.Error(h => h("[{0}] Error when shutting down working blocks in block container", this.Name), e);
                    if(originalException == null) originalException = e;
                }

                if (originalException != null)
                {
                    tcs.SetException(originalException);
                    this.Fault(new OtherBlockFailedException());
                }
                else if (task.Status == TaskStatus.Faulted)
                {
                    //Don't need to fault the whole container there because it is a NonOrignalException
                    Exception e = task.Exception.Flatten().InnerExceptions.First();
                    tcs.SetException(e);
                    Debug.Assert(e is PropagatedException);
                }
                else if (task.Status == TaskStatus.Canceled)
                {
                    tcs.SetCanceled();
                    this.Fault(new OtherBlockCanceledException());
                }
                else
                    tcs.SetResult(string.Empty);
            });

            m_blockMetas.Add(new BlockMeta
            {
                Block = block, 
                CompletionTask = tcs.Task, 
                CountGetter = countGetter ?? (() => block.GetBufferCount())
            });
        }

        protected void RegisterChildContainer(BlockContainer childContainer)
        {
            if (m_completionTask.IsValueCreated)
            {
                throw new InvalidOperationException("You cannot register block container after completion task has been generated. Please ensure you are calling RegisterChildContainer() inside constructor.");
            }

            foreach (BlockMeta blockMeta in childContainer.m_blockMetas)
            {
                m_blockMetas.Add(blockMeta);
            }
        }

        //todo: add completion condition and cancellation token support
        private async Task StartPerformanceMonitorAsync()
        {
            while (true)
            {
                await Task.Delay(m_containerOptions.MonitorInterval ?? TimeSpan.FromSeconds(10));

                if (m_containerOptions.ContainerMonitorEnabled)
                {
                    int count = this.BufferedCount;

                    if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                    {
                        LogHelper.Logger.Debug(h => h("[{0}] has {1} todo items at this moment.", this.Name, count));
                    }
                }

                if (m_containerOptions.BlockMonitorEnabled)
                {
                    foreach (BlockMeta bm in m_blockMetas)
                    {
                        IDataflowBlock block = bm.Block;
                        var count = bm.CountGetter();

                        if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                        {
                            LogHelper.Logger.Debug(h => h("[{0}->{1}] has {2} todo items at this moment.", this.Name, Utils.GetFriendlyName(block.GetType()), count));
                        }
                    }
                }
            }
        }

        protected virtual async Task GetCompletionTask()
        {
            if (m_blockMetas.Count == 0)
            {
                throw new NoBlockRegisteredException(this);
            }

            await TaskEx.AwaitableWhenAll(m_blockMetas.Select(b => b.CompletionTask).ToArray());
            this.CleanUp();
        }

        protected virtual void CleanUp()
        {
            //
        }

        /// <summary>
        /// Represents the completion of the whole container
        /// </summary>
        public Task CompletionTask
        {
            get
            {
                //todo: check multiple access and block registration
                return m_completionTask.Value;
            }
        }

        public virtual IEnumerable<IDataflowBlock> Blocks { get { return m_blockMetas.Select(bm => bm.Block); } }

        public virtual void Fault(Exception exception)
        {
            LogHelper.Logger.ErrorFormat("<{0}> Unrecoverable exception received. Shutting down my blocks...", exception, this.Name);

            foreach (var dataflowBlock in Blocks)
            {
                if (!dataflowBlock.Completion.IsCompleted)
                {
                    string msg = string.Format("<{0}> Shutting down {1}", this.Name, Utils.GetFriendlyName(dataflowBlock.GetType()));
                    LogHelper.Logger.Error(msg);
                    dataflowBlock.Fault(exception); //just use aggregation exception just like native link
                }
            }
        }

        /// <summary>
        /// Sum of the buffer size of all blocks in the container
        /// </summary>
        public virtual int BufferedCount
        {
            get
            {
                return m_blockMetas.Select(bm => bm.CountGetter).Sum(countGetter => countGetter());
            }
        }
    }

    public abstract class BlockContainer<TIn> : BlockContainer, IBlockContainer<TIn>
    {
        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
        }

        public abstract ITargetBlock<TIn> InputBlock { get; }
        
        /// <summary>
        /// Helper method to read from a text reader and post everything in the text reader to the pipeline
        /// </summary>
        public void PullFrom(IEnumerable<TIn> reader)
        {
            long count = 0;
            foreach(var item in reader)
            {
                InputBlock.SafePost(item);
                count++;
            }

            LogHelper.Logger.Info(h => h("<{0}> Pulled and posted {1} {2}s to the input block {3}.", 
                this.Name, 
                count, 
                Utils.GetFriendlyName(typeof(TIn)), 
                Utils.GetFriendlyName(this.InputBlock.GetType())
                ));
        }

        public void LinkFrom(ISourceBlock<TIn> block)
        {
            block.LinkTo(this.InputBlock, m_defaultLinkOption);
        }
    }

    public abstract class BlockContainer<TIn, TOut> : BlockContainer<TIn>, IBlockContainer<TIn, TOut>
    {
        protected List<Predicate<TOut>> m_conditions = new List<Predicate<TOut>>();
        protected StatisticsRecorder GarbageRecorder { get; private set; }

        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
            this.GarbageRecorder = new StatisticsRecorder();
        }

        public abstract ISourceBlock<TOut> OutputBlock { get; }
        
        protected void LinkBlockToContainer<T>(ISourceBlock<T> block, IBlockContainer<T> otherBlockContainer)
        {
            block.LinkTo(otherBlockContainer.InputBlock, new DataflowLinkOptions { PropagateCompletion = false });

            //manullay handle inter-container problem
            //we use WhenAll here to make sure this container fails before propogating to other container
            Task.WhenAll(block.Completion, this.CompletionTask).ContinueWith(whenAllTask => 
                {
                    if (!otherBlockContainer.CompletionTask.IsCompleted)
                    {
                        if (whenAllTask.IsCanceled || whenAllTask.IsFaulted)
                        {
                            otherBlockContainer.Fault(new OtherBlockContainerFailedException());
                        }
                        else
                        {
                            otherBlockContainer.InputBlock.Complete();
                        }
                    }
                });

            //Make sure 
            otherBlockContainer.CompletionTask.ContinueWith(otherTask =>
                {
                    if (this.CompletionTask.IsCompleted)
                    {
                        return;
                    }

                    if (otherTask.IsCanceled || otherTask.IsFaulted)
                    {
                        LogHelper.Logger.InfoFormat("<{0}>Downstream block container faulted before I am done. Shut down myself.", this.Name);
                        this.Fault(new OtherBlockContainerFailedException());
                    }
                });
        }

        public void LinkTo(IBlockContainer<TOut> other)
        {
            LinkBlockToContainer(this.OutputBlock, other);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, IMatchCondition<TOut> condition)
        {
            this.TransformAndLink(other, transform, new Predicate<TOut>(condition.Matches));
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, Predicate<TOut> predicate)
        {
            m_conditions.Add(predicate);
            var converter = new TransformBlock<TOut, TTarget>(transform);
            this.OutputBlock.LinkTo(converter, m_defaultLinkOption, predicate);
            
            LinkBlockToContainer(converter, other);            
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform)
        {
            this.TransformAndLink(other, transform, @out => true);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other) where TTarget : TOut
        {
            this.TransformAndLink(other, @out => { return ((TTarget)@out); }, @out => @out is TTarget);
        }

        public void TransformAndLink<TTarget, TOutSubType>(IBlockContainer<TTarget> other, Func<TOutSubType, TTarget> transform) where TOutSubType : TOut
        {
            this.TransformAndLink(other, @out => { return transform(((TOutSubType)@out)); }, @out => @out is TOutSubType);
        }

        public void LinkLeftToNull()
        {
            var left = new Predicate<TOut>(@out =>
                {
                    if (m_conditions.All(condition => !condition(@out)))
                    {
                        OnOutputToNull(@out);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                );
            this.OutputBlock.LinkTo(DataflowBlock.NullTarget<TOut>(), m_defaultLinkOption, left);
        }

        protected virtual void OnOutputToNull(TOut output)
        {
            this.GarbageRecorder.RecordType(output.GetType());
        }
    }
}