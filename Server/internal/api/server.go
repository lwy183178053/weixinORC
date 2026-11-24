package api

import (
	"net/http"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type Server struct {
	store    *store.Store
	verifier auth.Verifier
	sessions *auth.Sessions
}

func New(database *store.Store, verifier auth.Verifier, sessions *auth.Sessions) *gin.Engine {
	gin.SetMode(gin.ReleaseMode)
	engine := gin.New()
	_ = engine.SetTrustedProxies(nil)
	engine.Use(gin.Recovery(), limitBody())
	s := &Server{store: database, verifier: verifier, sessions: sessions}
	engine.GET("/healthz", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"status": "ok"}) })
	api := engine.Group("/api/v1")
	api.POST("/login", s.login)
	authed := api.Group("")
	authed.Use(sessions.Middleware())
	authed.POST("/logout", s.logout)
	authed.GET("/me", s.me)
	authed.GET("/customers", s.listCustomers)
	authed.POST("/customers", s.createCustomer)
	authed.GET("/customers/:id", s.getCustomer)
	authed.PATCH("/customers/:id", s.updateCustomer)
	authed.GET("/customers/:id/messages", s.listMessages)
	authed.POST("/customers/:id/messages", s.createMessage)
	return engine
}
