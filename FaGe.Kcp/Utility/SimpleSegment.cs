// Copyright 2025 Fa鸽. Licensed under The MIT License. 
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// TLDR: You can use this code freely in your projects, just remember to include the copyright notice.
//
// This is a self contained file and does not depend on any other files in the project.
// You can simply copy and paste this file into your project and use it as is.

using System.Buffers;

namespace DanmakuR.Protocol.Buffer;

/// <summary>
/// A simple implementation of <see cref="ReadOnlySequenceSegment{byte}"/> for creating linked segments of byte memory.
/// without any ownership or pooling logic.
/// In case of you manage the memory outside of this class, this class will not dispose or free the memory as you wish.
/// This is useful for testing or simple scenarios where you need to create a <see cref="ReadOnlySequence{byte}"/>.
/// </summary>
// 没想到多年前写的代码还能派上用场
internal class SimpleSegment : ReadOnlySequenceSegment<byte>
{
	public SimpleSegment(Memory<byte> buff, long runningIndex)
	{
		Memory = buff;
		RunningIndex = runningIndex;
	}

	public SimpleSegment(byte[] buff, long runningIndex) : this(buff.AsMemory(), runningIndex)
	{

	}

	public SimpleSegment SetNext(byte[] buff)
	{
		var next = new SimpleSegment(buff.AsMemory(), RunningIndex + Memory.Length);
		Next = next;
		return next;
	}

	public SimpleSegment SetNext(Memory<byte> buff)
	{
		var next = new SimpleSegment(buff, RunningIndex + Memory.Length);
		Next = next;
		return next;
	}
}