# build
FROM golang:1.22-alpine AS build
WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o /bin/cloud-api ./cmd/api

# run
FROM alpine:3.20
RUN adduser -D -H appuser
USER appuser
WORKDIR /home/appuser
ENV HTTP_PORT=8080
EXPOSE 8080
COPY --from=build /bin/cloud-api /usr/local/bin/cloud-api

ENTRYPOINT ["/usr/local/bin/cloud-api"]
