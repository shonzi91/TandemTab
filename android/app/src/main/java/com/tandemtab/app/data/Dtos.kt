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
data class ExternalProvidersDto(
    val google: Boolean = false,
    val facebook: Boolean = false,
)

@Serializable
data class ExchangeCodeRequest(val code: String)

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
data class LogoutRequest(val refreshToken: String)

@Serializable
data class TwoFactorLoginRequest(val ticket: String, val code: String)

@Serializable
data class MemberDto(
    val userId: String,
    // Server sends "displayName" (FinApp.Contracts.MemberDto), not "username".
    val displayName: String = "",
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

@Serializable
data class ExpenseDto(
    val id: String,
    val categoryId: String,
    val categoryName: String,
    val categoryIcon: String? = null,
    val fundId: String,
    val fundName: String,
    val amount: Double,
    val date: String, // ISO yyyy-MM-dd
    val note: String? = null,
    val autoFiled: Boolean = false,
    val fromSavings: Boolean = false,
    val onBehalfOfOtherAccount: Boolean = false,
    val isSettlementSource: Boolean = false,
    val isSettlementDestination: Boolean = false,
)

@Serializable
data class CategoryOptionDto(val id: String, val name: String, val icon: String? = null, val parentId: String? = null)

@Serializable
data class FundOptionDto(val id: String, val name: String, val synced: Boolean = false)

@Serializable
data class SpendingViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val expenses: List<ExpenseDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
)

// --- Goals / Savings (mirrors FinApp.Contracts.SavingsView) ------------------------------------------
// Every figure resolved server-side; the client just renders. Kind is "goal"/"debt"/"investment"/"sinking".

@Serializable
data class SavingBucketDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val saved: Double,
    val kind: String,
    val archived: Boolean = false,
    val goalTarget: Double? = null,
    val goalProgress: Double? = null,     // 0..1
    val debtBalance: Double? = null,       // owed today
    val debtProgress: Double? = null,      // 0..1 paid off
    val debtMonthsAhead: Int? = null,
    val investmentProjected: Double? = null,
    val monthlySetAside: Double? = null,
    val targetShortfall: Double? = null,
)

@Serializable
data class SavingsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val availableToSave: Double = 0.0,
    val maxAdditionalSavings: Double = 0.0,
    val buckets: List<SavingBucketDto> = emptyList(),
)

// --- Wallets / Funds (mirrors FinApp.Contracts.WalletsView) ------------------------------------------

@Serializable
data class FundRowDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val note: String? = null,
    val balance: Double,
    val openingBalance: Double = 0.0,
    val synced: Boolean = false,
    val archived: Boolean = false,
    val availableToTransferOut: Double = 0.0,
)

@Serializable
data class FundTransferRowDto(
    val id: String,
    val fromFundId: String,
    val fromFundName: String,
    val toFundId: String,
    val toFundName: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

@Serializable
data class WalletsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val funds: List<FundRowDto> = emptyList(),
    val archivedFunds: List<FundRowDto> = emptyList(),
    val transfers: List<FundTransferRowDto> = emptyList(),
)
