﻿using MediatR;

namespace Veyra.Application.Commands.Setup;

public sealed record UpdateWatchedDirectoryCommand(string OldPath, string NewPath) : IRequest;
