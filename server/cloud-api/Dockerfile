# build
FROM golang:1.25-alpine AS build
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY . .

ARG TARGETOS
ARG TARGETARCH
ENV CGO_ENABLED=0

RUN GOOS=$TARGETOS GOARCH=$TARGETARCH \
    go build -trimpath -ldflags="-s -w" -o /out/cloud-api ./cmd/api

# run
FROM alpine:3.20
RUN adduser -D -H appuser \
    && mkdir -p /var/lib/veyra/blocks \
    && chown -R appuser:appuser /var/lib/veyra
USER appuser
WORKDIR /home/appuser

ENV HTTP_PORT=8080
ENV BLOCK_STORE_DIR=/var/lib/veyra/blocks
EXPOSE 8080

COPY --from=build /out/cloud-api /usr/local/bin/cloud-api
ENTRYPOINT ["/usr/local/bin/cloud-api"]

