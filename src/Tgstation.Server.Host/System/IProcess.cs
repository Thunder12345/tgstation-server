﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tgstation.Server.Host.System
{
	/// <summary>
	/// Abstraction over a <see cref="global::System.Diagnostics.Process"/>.
	/// </summary>
	interface IProcess : IProcessBase, IAsyncDisposable
	{
		/// <summary>
		/// The <see cref="IProcess"/>' ID.
		/// </summary>
		int Id { get; }

		/// <summary>
		/// The <see cref="Task"/> representing the time until the <see cref="IProcess"/> becomes "idle".
		/// </summary>
		Task Startup { get; }

		/// <summary>
		/// Get the stderr and stdout output of the <see cref="IProcess"/>.
		/// </summary>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the stderr and stdout output of the <see cref="IProcess"/>.</returns>
		/// <remarks>
		/// To guarantee that all data is received from the <see cref="IProcess"/> when redirecting streams to a file
		/// the result of this function must be <see langword="await"/>ed before <see cref="IDisposable.Dispose"/> is called.
		/// </remarks>
		Task<string> GetCombinedOutput(CancellationToken cancellationToken);

		/// <summary>
		/// Asycnhronously terminates the process.
		/// </summary>
		/// <remarks>To ensure the <see cref="IProcess"/> has ended, use the <see cref="IProcessBase.Lifetime"/> <see cref="Task{TResult}"/>.</remarks>
		void Terminate();

		/// <summary>
		/// Get the name of the account executing the <see cref="IProcess"/>.
		/// </summary>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the name of the account executing the <see cref="IProcess"/>.</returns>
		Task<string> GetExecutingUsername(CancellationToken cancellationToken);
	}
}
