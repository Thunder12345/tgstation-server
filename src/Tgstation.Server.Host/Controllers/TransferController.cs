﻿using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Tgstation.Server.Api;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Host.Database;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.Security;
using Tgstation.Server.Host.Transfer;

namespace Tgstation.Server.Host.Controllers
{
	/// <summary>
	/// <see cref="ApiController"/> for file streaming.
	/// </summary>
	[Route(Routes.Transfer)]
	[RequestSizeLimit(Limits.MaximumFileTransferSize)]
	public sealed class TransferController : ApiController
	{
		/// <summary>
		/// The <see cref="IFileTransferStreamHandler"/> for the <see cref="TransferController"/>.
		/// </summary>
		readonly IFileTransferStreamHandler fileTransferService;

		/// <summary>
		/// Initializes a new instance of the <see cref="TransferController"/> class.
		/// </summary>
		/// <param name="databaseContext">The <see cref="IDatabaseContext"/> for the <see cref="ApiController"/>.</param>
		/// <param name="authenticationContextFactory">The <see cref="IAuthenticationContextFactory"/> for the <see cref="ApiController"/>.</param>
		/// <param name="fileTransferService">The value of <see cref="fileTransferService"/>.</param>
		/// <param name="logger">The <see cref="ILogger"/> for the <see cref="ApiController"/>.</param>
		public TransferController(
			IDatabaseContext databaseContext,
			IAuthenticationContextFactory authenticationContextFactory,
			IFileTransferStreamHandler fileTransferService,
			ILogger<ApiController> logger)
			: base(
				  databaseContext,
				  authenticationContextFactory,
				  logger,
				  true)
		{
			this.fileTransferService = fileTransferService ?? throw new ArgumentNullException(nameof(fileTransferService));
		}

		/// <summary>
		/// Downloads a file with a given <paramref name="ticket"/>.
		/// </summary>
		/// <param name="ticket">The <see cref="FileTicketResponse.FileTicket"/> for the download.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the method.</returns>
		/// <response code="200">Started streaming download successfully.</response>
		/// <response code="410">The <paramref name="ticket"/> was no longer or was never valid.</response>
		[TgsAuthorize]
		[HttpGet]
		[ProducesResponseType(200, Type = typeof(LimitedStreamResult))]
		[ProducesResponseType(410, Type = typeof(ErrorMessageResponse))]
		public Task<IActionResult> Download([Required, FromQuery] string ticket, CancellationToken cancellationToken)
			=> fileTransferService.GenerateDownloadResponse(this, ticket, cancellationToken);

		/// <summary>
		/// Uploads a file with a given <paramref name="ticket"/>.
		/// </summary>
		/// <param name="ticket">The <see cref="FileTicketResponse.FileTicket"/> for the upload.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the method.</returns>
		/// <response code="204">Uploaded file successfully.</response>
		/// <response code="409">An error occurred during the upload.</response>
		/// <response code="410">The <paramref name="ticket"/> was no longer or was never valid.</response>
		[TgsAuthorize]
		[HttpPut]
		[ProducesResponseType(204)]
		[ProducesResponseType(410, Type = typeof(ErrorMessageResponse))]
		public async Task<IActionResult> Upload([Required, FromQuery] string ticket, CancellationToken cancellationToken)
		{
			if (ticket == null)
				return BadRequest(new ErrorMessageResponse(ErrorCode.ModelValidationFailure));

			var fileTicketResult = new FileTicketResponse
			{
				FileTicket = ticket,
			};

			var result = await fileTransferService.SetUploadStream(fileTicketResult, Request.Body, cancellationToken);
			if (result != null)
				return result.ErrorCode == ErrorCode.ResourceNotPresent
					? this.Gone()
					: Conflict(result);

			return NoContent();
		}
	}
}
