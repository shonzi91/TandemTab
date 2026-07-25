package com.tandemtab.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Wire models mirroring FinApp.Contracts. The server serializes records with camelCase property names
 * (System.Text.Json default), so these match by field name; @SerialName is used only where the Kotlin
 * name would differ.
 */

@Serializable
data class LoginRequest(
    val usernameOrEmail: String,
    val password: String,
)

@Serializable
data class RegisterRequest(
    val username: String,
    val email: String,
    val password: String,
)

@Serializable
data class ForgotPasswordRequest(val identifier: String)

@Serializable
data class AuthResponse(
    val token: String,
    val userId: String,
    val username: String,
    val email: String,
    val expiresAt: String,
    val refreshToken: String? = null,
)

@Serializable
data class LoginResponse(
    val twoFactorRequired: Boolean,
    val auth: AuthResponse? = null,
    val twoFactorTicket: String? = null,
)

@Serializable
data class RefreshRequest(val refreshToken: String)

@Serializable
data class MemberDto(
    val userId: String,
    val username: String,
)

@Serializable
data class AccountSummaryDto(
    val id: String,
    val name: String,
    val currency: String,
    val ownerUserId: String,
    val isOwner: Boolean,
    val members: List<MemberDto> = emptyList(),
)

@Serializable
data class AccountOverviewDto(
    val currency: String,
    val current: Double,
    val free: Double,
    val saved: Double,
    val spent: Double,
    val contributed: Double,
    val billsDue: Double,
    val safeAfterBills: Double,
)
