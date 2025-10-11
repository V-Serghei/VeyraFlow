package main

import (
	"context"
	"fmt"
	"log"
	"net"

	"google.golang.org/grpc"
)

// Server represents the gRPC server
type Server struct {
	// UnimplementedSyncServiceServer
}

// SyncFile handles file synchronization requests
func (s *Server) SyncFile(ctx context.Context, req *SyncFileRequest) (*SyncFileResponse, error) {
	log.Printf("Sync request for file: %s, size: %d", req.FilePath, req.FileSize)
	return &SyncFileResponse{
		Success: true,
		Message: "File sync initiated",
	}, nil
}

// GetStatus returns the sync agent status
func (s *Server) GetStatus(ctx context.Context, req *StatusRequest) (*StatusResponse, error) {
	return &StatusResponse{
		Online:       true,
		FilesSynced:  0,
		LastSyncTime: 0,
	}, nil
}

func main() {
	port := 50051
	lis, err := net.Listen("tcp", fmt.Sprintf(":%d", port))
	if err != nil {
		log.Fatalf("Failed to listen: %v", err)
	}

	grpcServer := grpc.NewServer()
	// RegisterSyncServiceServer(grpcServer, &Server{})

	log.Printf("VeyraFlow Sync Agent starting on port %d...", port)
	if err := grpcServer.Serve(lis); err != nil {
		log.Fatalf("Failed to serve: %v", err)
	}
}

// Placeholder protobuf types (will be generated from proto file)
type SyncFileRequest struct {
	FilePath    string
	ContentHash []byte
	FileSize    int64
}

type SyncFileResponse struct {
	Success bool
	Message string
}

type StatusRequest struct {
	ClientId string
}

type StatusResponse struct {
	Online       bool
	FilesSynced  int64
	LastSyncTime int64
}
