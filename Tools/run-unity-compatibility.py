"""Run focused real-Unity tests in an explicit project; never build or publish an app."""
import argparse
import json
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path


NAMESPACE = 'VRVlog.LilToonExporter.Tests.'
PROFILES = ('compatibility', 'exporter-behavior', 'exporter-integration')
# Run whole classes, or named methods of suites shared with optional integration.
# Named cases also prevent a removed regression or one lost
# parameter variant from turning a smaller, passing XML into release evidence.
BEHAVIOR_CASES = {
    'MissingMeshAttributesTests': {
        'AdditionalVertexStreamsPreserveAuthoredChannelInsteadOfUsingFallback': 4,
        'DegenerateGeometryReceivesFiniteUnitNormalsWithoutLosingMorphs': 2,
        'DirectUniVrmExportRepairsOnlyAbsentAttributesAndKeepsAuthoredMorphs': 8,
        'FailureDuringExportRestoresMissingAttributeSourceMesh': 2,
        'MissingNormalsWithoutMorphsAlsoExportsThroughMeshWriter': 2,
        'MissingUv0NeverBorrowsASecondUvChannel': 2,
        'NormalExporterPreservesMissingAttributeMeshesThroughRealVrmRoundTrip': 12,
        'SharedStaticMeshRepairsEveryExportedMeshGroupWithoutSplittingSourceIdentity': 1,
        'SharedStaticMeshKeepsEachRenderersAdditionalStreamsInFullBindings': 2,
    },
    'AppearanceRecoveryTests': {
        'ActiveOrKeywordEnabledLayersKeepTheOriginalDynamicImageDiagnostic': 3,
        'EmptyMeshDoesNotCreateAFullBindingOrRemoveItsBone': 4,
        'FullExportAcceptsAnInactiveDynamicLayerWithoutEditingSourceAppearance': 1,
        'LiveOrConsumedMaterialAnimationPreventsInactiveTextureOmission': 4,
        'MaterialPropertyBlockPreventsInactiveTextureOmission': 1,
        'NonemptyUnsupportedTopologyIsNotClassifiedAsEmpty': 1,
        'SupportedImageInAnInactiveLayerIsStillStored': 1,
        'UnsupportedImageInAProvenInactiveLayerKeepsEveryMaterialProperty': 16,
        'UnsupportedImagesOutsideTheProvenLayerSetAreNotOmitted': 2,
    },
    'BlinkExportTests': {
        'ACompleteBilateralShapeCanCoverAnIncompleteAlternativePair': 1,
        'ACompleteRendererPairKeepsPriorityOverAlternatePartialFamilies': 1,
        'ACompleteUnifiedPairDoesNotAdoptAnUnrelatedLegacyPartial': 1,
        'ACompleteUnifiedPairIncludesCompatibleOneSidedEyelashes': 4,
        'APartialEyelashPairCannotBeHiddenByAnotherRenderersBlink': 2,
        'AUnifiedPartialKeepsPriorityOverMultipleLegacyPartialFamilies': 1,
        'ActualExportPreservesAuthoredClipsIncludingExplicitEmpty': 2,
        'ActualUniVrmExportBindsGeneratedTargetsToTheirFinalNodes': 2,
        'AllBlinkRenderersMustSupportTheIndividualPair': 1,
        'AllPartialFamiliesRemainAvailableForTheirMatchingOtherRenderer': 2,
        'AmbiguousOrPartialNamesNeedAnExplicitChoice': 3,
        'CompatibleMergedAutomaticBlinkKeepsOneBindingAndTheSourceEndpoint': 2,
        'CompleteUnifiedBlinkPairWinsBeforeAnEarlierLegacySingleSide': 1,
        'ConflictingMergedAutomaticEndpointsLeaveEverySourceBindingUsable': 2,
        'CrossRendererLegacyPairsDoNotHideAnUnmatchedThirdRenderer': 1,
        'CrossRendererLegacySidesCannotMixNameFamilies': 1,
        'DescriptorBlinkUsesOnlyTheClosedSlotAndValidatesMissingIndices': 1,
        'DisjointMergedAutomaticBlinkChannelsKeepBothSidesAndTheirEndpoints': 1,
        'DuplicateSemanticNamesAreNotGuessed': 1,
        'EmptyAuthoredVrmClipIsPreservedUnlessExplicitlyOverridden': 1,
        'ExplicitBindingsRequireExactNamesAndRejectRepeatedChannels': 1,
        'ExplicitManualChoiceResolvesCaseDistinctAliasesAndBakesTheSelectedChannel': 2,
        'ExplicitTrackingMarkerKeepsPartialBlinkValidation': 1,
        'EyeSpacingNeverWinsOverAnEyelidShape': 1,
        'FailedAutomaticSetupOffersAnEmptyManualRowWithoutGuessing': 1,
        'InertPreferredUnifiedAliasCannotSelectTheMovingLowerAlias': 1,
        'InertUnifiedClosureCannotWaiveMissingBlinkForSharedJaw': 2,
        'InertUnifiedClosureIsNotSelectedAlongsideUsableExplicitJaw': 4,
        'LeftAndRightMustBelongToOneNameFamily': 1,
        'LegacyEyesOnDifferentRenderersResolveWithoutTrackingEvidence': 5,
        'ManualAdjustmentCopiesTheResolvedBindingsWithoutChangingTheAutomaticResult': 1,
        'ManualChoiceOverridesInferenceAndRejectsForeignOrRemovedShapes': 1,
        'MergedBlinkRetainsDeformationChecksForEveryCapturedReference': 2,
        'MergedManualBlinkCoalescesOnlyMatchingClosureAmounts': 2,
        'MergingIndependentLeftAndRightBlinkCannotTurnThemIntoOneSharedChannel': 1,
        'MissingMergedBlinkRendererCannotPartiallyPublishEndpointRelocation': 1,
        'NoneProducesAnInertBindingWithoutDestroyingAuthoredMorphs': 1,
        'PairOnlyEyelashesContributeToTheBilateralFallback': 1,
        'PartialClosureIsBakedRelativeToTheAuthoredRestWithoutTouchingSource': 1,
        'PartialUnifiedBlinkWinsOverLegacyPartialBeforeCrossRendererPairing': 1,
        'PartialUnifiedClosureDoesNotInventBilateralBlink': 1,
        'PreviewOwnsItsCopyAndCleansItUpWithoutChangingTheSource': 1,
        'PreviewRendersOpenClosedAndOpenWithinOneUpdateWithoutChangingSource': 1,
        'PreviewUsesTheBilateralPresetAndFallsBackOnlyWhenItIsAbsent': 2,
        'SourceRendererIdentitySurvivesRenameAndSiblingReorder': 1,
        'StatusClearsAnEarlierSuccessWhenTheLiveBindingBecomesInvalid': 1,
        'StatusReusesScansUntilExpiryOrARelevantSettingChanges': 1,
        'TheFirstCompatiblePartialFamilyOwnsEachRenderer': 1,
        'UnifiedEyesOnDifferentRenderersFormOneBilateralClosure': 1,
    },
    'LegacyBlinkMenuRoundTripTests': {
        'SplitLegacyBlinkAndDistinctNamedMenusKeepTheirAppearanceAfterReimport': 2,
    },
    'PhysBoneSpringExportTests': {
        'AParentTailCannotShareTheChildOwnersFirstJoint': 1,
        'AbsentSdkAndRigidModelsRemainValidWithoutInventedSprings': 1,
        'ActualExportImportsNestedOwnersWithoutSharedJointsOrChangedAppearance': 2,
        'ActualExportImportsSpringsAndMovesHairWithoutChangingOriginal': 4,
        'AuthoredRootColliderSurvivesWithoutAnyPhysBoneOrSdkDependency': 1,
        'BranchesAndVirtualEndpointsHaveOneOwnerAndKeepSource': 3,
        'CurvesCollidersAndLimitsUseRealSdkSerializedValues': 1,
        'EmptyOrTruncatedSerializedSpringsPreventSaving': 1,
        'ExistingTerminalFieldsAndVerificationBaselinesAreProtected': 1,
        'ExistingVrmSettingsWinAndRemovedExplicitRootsDoNotAnimateTheBody': 1,
        'GeneratedEndpointsDoNotTurnAnInertNestedPhysBoneIntoAnotherDriver': 1,
        'IgnoredSubtreesDisabledBonesAndOverlappingOwnersAreExplicit': 1,
        'InactiveExplicitRootsChildrenAndColliderRootsAreSkipped': 1,
        'MalformedPhysBoneDataStillStopsConversion': 3,
        'NestedOwnersKeepRestShapeForcesAndCollidersRegardlessOfComponentTraversal': 2,
        'PreexistingVrmImplicitJointsAndTerminalsWinWhileDisjointSegmentsSurvive': 1,
        'SameRootPrefersAttachedComponentThenStableComponentOrder': 2,
        'ZeroLengthPairsKeepBonesAndOriginalDepthForTheRemainingChain': 1,
    },
    'SourceFingerprintCacheTests': {
        'ColdClipEditorCacheDoesNotInvalidateButNativeCurveEditsDo': 1,
        'MaterialDefaultCachePopulationDoesNotInvalidateButAnEffectiveEditDoes': 3,
        'UnknownSavedMaterialPropertiesAndTextureTransformsStillInvalidate': 1,
    },
    'AdditionalPlayableCallbackTests': {
        'DormantSdkFxWeightCommandsPermitNeutralAndFixedSelectionWithoutSourceMutation': 4,
        'DormantNestedFxWeightCommandsAndTheirExitRestoresArePruned': 2,
        'ReachableSdkFxWeightCommandsRemainUnsupported': 7,
        'ReachableInstantAndTimedSdkCommandsAreNotExecutedOrIgnored': 4,
        'EveryRawParameterWriterPreventsDormantCommandProof': 7,
        'ConflictingThirdPlayableParameterTypeCannotCertifyAConstant': 2,
        'ExplicitChangedNormalInputCannotPruneAnAdditionalCommand': 1,
        'LegacySamplingWithoutNormalInputContextRetainsDormantCommands': 1,
        'GenuineUnknownCallbacksInvalidateTheWholeAdditionalProof': 2,
        'AnimatorLayerMetadataDistinguishesValidBodyTargetsFromMalformedCommands': 11,
        'ReachableAdditionalMorphWriterRemainsHardDespiteKnownWeightCommand': 1,
        'UnprovedTypedGateInputsDoNotHideCommands': 2,
    },
    'TemporalNeutralShapeTests': {
        'TemporalRestKeepsPreparedWeightAndReconstructsTheIndependentConstant': 3,
        'MixedIdleKeepsAllSeventeenTemporalAndSamplesAllOneHundredFifteenStaticChannels': 1,
        'InactiveAlternateTemporalClipDoesNotChangeTheActiveConstantCapture': 1,
        'TemporalCaptureKeepsIndependentNativeOverrideAdditiveAndWriteDefaults': 4,
        'OnlyAFullUnmaskedExplicitConstantOverrideDominatesTheLowerTemporalCurve': 6,
        'UnresolvedTemporalGraphKeepsPreparedRestButStillRejectsUnsupportedDrivers': 4,
        'DirectFixedExpressionSamplingStillRejectsItsActualTemporalCurve': 1,
        'TemporalRequiredEndpointExportsPreparedRestAndTheExactAuthoredProgram': 2,
    },
    'NeutralShapeSamplerTests': {
        'UnknownComponentPropertyPreservesPreparedNeutralAndReportsTarget': 1,
        'KnownConstraintSupportKeepsNativeWriteDefaultsAndIndependentMorphRest': 4,
        'ConstraintCoupledMorphKeepsPreparedWeightWhileIndependentFaceStillSamples': 2,
        'ConstraintEnabledSupportKeepsPreparedRestWithoutExecutingConstraint': 2,
        'ConstraintCoupledExplicitEndpointKeepsPreparedNeutralForLaterEndpointBaking': 1,
        'UnknownPropertyFallbackCannotHideLaterActiveNaNOrAnimationEvent': 2,
        'AppearanceOnlyPreparedRestRetainsKnownFxWeightButRejectsUnknownPlayable': 2,
        'FractionalOrMaskedTopOverrideCannotProveIndependenceFromUnmeasuredLowerInputs': 2,
        'FutureNonMorphChangeBelowTopOverrideRetainsPreparedNeutral': 3,
        'FutureInvalidParameterDriverBelowTopOverrideStillFailsDataChecks': 1,
        'UnmeasuredBuiltinInputKeepsPreparedNeutralInsteadOfChoosingAFace': 2,
        'UnresolvedGroupPreservesAuthoredWeightsWithoutDiscardingIndependentRest': 2,
        'RecoverableExternalInputRetainsKnownFxWeightButRejectsUnknownOrOtherMorphWriters': 3,
        'DelayedGenericMorphAnimationKeepsPreparedRestInsteadOfASampledPhase': 1,
        'AutomaticAnimationSharingTheOpeningChannelKeepsItsPreparedRest': 1,
        'AutomaticBlinkWithWriteDefaultsKeepsTheFullNativeClosureAndConstantRest': 1,
        'CurrentCapHairAndMaskStayTogetherWhileIndependentFaceAndPermanentMorphReconstruct': 2,
        'SharedCustomAppearanceControlKeepsSeparateVisibilityAndMaskTogetherWithoutClaimingGenericResetFace': 6,
        'AppearanceOwnershipPrunesOnlyProvedNormalExternalBranches': 7,
        'OptionalUnboundOrUnexportedMorphCannotPoisonIndependentFaceReconstruction': 7,
        'AppearanceSupportThatDisablesTheRendererKeepsPreparedNeutralAndActivation': 2,
        'IndependentTransformSupportCapturesNativeMorphScalarsAndKeepsPreparedPose': 3,
        'IndependentTransformSupportKeepsNativeScalarAcrossOverlappingMorphFrames': 6,
        'IndependentTransformOnSixthSkinInfluenceKeepsNativeScalarAndPreparedBone': 1,
        'IndependentTransformSupportStillRejectsUnknownPropertyComponents': 4,
        'MissingTypedRendererComponentCannotClaimOrBlockARequiredMorph': 2,
        'DisjointAdditiveVisibilityOrMaterialSupportKeepsNativeMorphMathAndPreparedAppearance': 2,
        'ExplicitEndpointRequirementKeepsItsCoupledPreparedNeutral': 1,
        'NeutralUsesTheSameNormalVrChatInputsAsMenuSampling': 4,
        'NeutralContactUsesTheAuthoredDefaultWithoutReceivingLiveInput': 4,
        'DormantAfkActionCannotChangeTheNormalNeutralFace': 2,
        'UnsavedNonMenuSignalWithOnlyDormantOtherPlayableWritersDoesNotOwnTheNormalFace': 2,
        'SavedExposedUnprovedOrWrittenCustomSignalRetainsItsAppearanceAlternatives': 17,
        'TransientOwnershipProofRetainsUnknownMetadataAndExplicitOrRemoteInputs': 5,
        'CompleteMenuInputInventoryHandlesSharedCyclicAndEmptyOptionalSdkInputs': 3,
        'FixedMenuAcceptsOnlyProvenUnchangedOrUnboundActivation': 2,
    },
    'NeutralLayerControlTests': {
        'MmdRelayRetainsPreparedTargetAndIndependentTopOverrideWithoutNameExceptions': 2,
        'IndividualLayerControlKeepsPreparedRestWithoutAssumingItsGoalIsAlreadyApplied': 4,
        'FxBaseLayerIndexZeroIsValidMetadataWithoutInventingItsRuntimeEffect': 1,
        'AControlOfTheTopLayerOrWholeFxCannotCertifyIndependentNeutral': 2,
        'NonFxIndividualLayerControlsDoNotBlockNeutralMorphs': 6,
        'NormalTypedInputsCanProveAValidLayerControlDormant': 3,
        'AReachableAdditionalPlayableFxCommandKeepsPreparedNeutralWithoutRunningCallbacks': 2,
        'MalformedSdkLayerControlDataRemainsHardEvenOnADormantBranch': 10,
        'KnownLayerControlFallbackCannotHideUnknownCallbacksOrBadAnimationData': 3,
        'AdditionalFxControlCannotHideReachableBadDataEvenWhenEveryNeutralIsRetained': 3,
        'ProvenDormantAdditionalBadDataDoesNotBlockPreparedNeutralFallback': 2,
        'InvalidFxDriverReadOnlyByAnotherPlayableRemainsHardBeforeLayerControlFallback': 1,
        'DirectClipEarlyReturnsStillValidateAdditionalFxEffects': 4,
        'ExplicitSelectedExpressionsEvaluateInstantFxLayerControlsAndRejectUnmodelledEffects': 4,
        'MmdControlledPreparedRestAndAuthoredEndpointSurviveFullExportAndVrmRoundTrip': 1,
    },
    'SelectedLayerControlTests': {
        'InstantIndividualFxControlMatchesNativeSelectedWeight': 9,
        'SelectedControlCanEnableAStaticInitiallyZeroTarget': 1,
        'LayerRelaySelectionUsesStateEntryOrderAndFreshProbes': 1,
        'GeneratedConstantSignalRelayDoesNotDiscardSelectedExpressions': 2,
        'DirectClipControlsPreserveNativeSlotAndStandaloneIndexMapping': 7,
        'ConflictingIndividualCommandsCannotChooseCallbackOrder': 2,
        'UnsupportedIndividualControlTimingAndCallbacksStayStrict': 4,
        'NonFxIndividualControlsRemainOutsideFacialWeightEvaluation': 3,
        'BaseLayerControlUsesTheNativeUnitWeightContract': 2,
        'SelectedLayerControlMenuSurvivesFullVrmRoundTrip': 1,
        'InactiveDirectClipDistinguishesDormantEnableFromReachedExplicitDisable': 2,
        'ZeroWeightControlSourceCannotHideALaterParameterDrivenWeightChange': 1,
        'StandaloneClipKeepsOriginalSdkBaseLayerAtUnitWeightAfterIndexRemap': 1,
        'DirectBaseClipWithoutStateProvenanceCannotDiscardItsLayerControl': 1,
        'InactiveSelectedStateKeepsItsOwnWeightButExecutesAControlOfAnotherLayer': 1,
        'SelectedCallbackRetainsItsUpperNonEquivalentSelectorTarget': 2,
    },
    'SelectedExpressionAppearanceTests': {
        'UnchangedPreparedAppearanceKeepsOnlyAuthoredMorphAnimation': 7,
        'ARealAppearanceChangeStillRejectsTheWholeSelectedClip': 4,
        'AChangingAppearanceCurveIsNotCertifiedFromItsFirstKey': 3,
        'PropertyBlockPreventsMaterialNoOpProofEvenWhenStoredMaterialMatches': 2,
        'EverySharedMaterialSlotMustHaveTheSameDeclaredValue': 3,
        'NoOpCandidateCannotConcealCorruptOrUnsupportedClipData': 4,
        'AmbiguousPathsRemainHardEvenWhenBothTargetsHaveMatchingValues': 1,
        'ExistingUnknownPropertiesAreNotAuthorizedByCoincidentValues': 5,
        'UnusedEndpointTangentsDoNotInvalidateAnOtherwiseConstantNoOp': 1,
        'NoOpProofUsesThePreparedCopyRatherThanAnEarlierAuthoringValue': 2,
        'MixedLilToonGesturePreservesPreparedAppearanceAndMorphEndpointsAfterExport': 4,
        'MatchingShaderColorChannelsKeepTheirNativeAppliedValue': 2,
        'MaterialColorAnimationMatchesActualRenderedPixels': 6,
        'ColorCanonicalizationDoesNotRelaxOtherPropertyDomains': 3,
    },
    'VrChatMenuExpressionTests': {
        'UnsupportedVisibilityIsReportedInsteadOfDroppingHalfAnExpression': 1,
        'GestureClipKeepsChangingCurvesButRejectsPartialMaterialFaces': 1,
    },
    'ParameterDriverExpressionTests': {
        'FixedMenuPrunesDormantAfkActionButRejectsExplicitlySelectedAfk': 2,
        'FixedContactInputUsesAuthoredDefaultAndAllowsExplicitSelectionOverride': 1,
        'AnotherPlayableWriterInvalidatesFalseAndGuardProof': 1,
    },
    'MergedFxDefaultsTests': {
        'DirectClipProbeAppliesOriginalAnimatorLayerControlUsingNativeWeights': 4,
        'DirectGestureProbeUsesNormalAndAuthoredContactInputs': 5,
        'DirectGestureProbeStillRejectsUnresolvedExternalInputs': 2,
        'DirectScalarSupportKeepsPreparedInfluencingBoneAndWardrobeMorphs': 2,
        'DirectScalarSupportCannotDisableTheCapturedRenderer': 1,
        'DirectScalarProbeDefersUnknownAuthoredChannelsToPreparedResolution': 4,
        'ProvenBaseStateRetainsItsCallbackAndTheDynamicUpperNativeGraph': 1,
        'InactiveProvenStateKeepsAuthoredClipUnlessItsCallbacksAffectRetainedGraph': 3,
        'ProvenMovingDirectClipKeepsNativeSupportWithoutAnUnrelatedUpperExpressionReset': 4,
        'ProvenMovingDirectClipStillHonorsAProvenPermanentUpperOverride': 2,
        'ProvenMovingDirectClipRetainsTheConfiguredSingleStateBlendTreeSupport': 1,
        'SourceFrameRangePolicyPreservesNativeClampedGeometryAndUnlimitedAuthoredMath': 2,
        'FractionalStationaryFxGeometrySurvivesOneClickExportAndVrmReimport': 2,
        'NeutralAdditiveDisjointMorphGroupsMatchTheAuthoredNativeController': 4,
        'NeutralAdditiveAutomaticSupportKeepsNativeDefaultsWithoutBakingMovingBlink': 1,
        'InstalledMaReparentsFaceEmoRendererAndMergesPermanentPupilFxBeforeEvaluation': 1,
    },
    'DirectExpressionNativeLayersTests': {
        'FractionalPlayerBlendsWithTheEvaluatedLowerDefaultAcrossPossibleTransitions': 2,
        'GestureRegistrationRetainsTheExactStatesDriverAndEffectiveClip': 2,
        'InvalidSelectedStateProvenanceCannotRewriteTheDirectEntry': 3,
        'SharedGestureClipKeepsDistinctNativeOutcomesFromItsSelectedCallbacks': 1,
    },
    'FaceEmoPreparedFxIntegrationTests': {
        'RegisteredPlayerCallbacksFollowExactOrProvedRetargetedClipProvenance': 3,
        'RegisteredPlayerRejectsAmbiguousMissingOrChangedCallbackProvenance': 4,
        'RegisteredPlayerRejectsOriginalIdentityWithDifferentEffectiveOverride': 1,
        'RegisteredPlayerReadsRegistryProvenPreparedAugmentationAndExactCallbacks': 2,
        'RegisteredPlayerRejectsUnprovedOrAmbiguousPreparedAugmentation': 4,
        'RegisteredPlayerRejectsPreparedMotionChangesBeforeDeferredEvaluation': 2,
    },
    'MergedFixedNeutralSamplingTests': {
        'FractionalNeutralRetainsNativeBaseActivityAndLeavesAutomaticBlinkLive': 4,
        'FractionalNeutralKeepsPreparedRestForDelayedAutomaticBaseActivityChanges': 1,
        'FractionalNeutralDistinguishesDisconnectedAndReachableUnsupportedSupportMotions': 4,
    },
    'NeutralCurveConditionTests': {
        'DynamicParameterCurveKeepsPreparedNeutralWhenItsFutureTimedExitIsUnresolved': 1,
        'CompetingParameterDriverPreventsTheRelayProof': 1,
    },
    'NeutralParameterCurveTests': {
        'IndependentParameterAnimationKeepsTheNativeBodyPose': 6,
        'ParameterThatControlsCapturedMorphsMustRemainFixedEvenWhenItsChangeIsDelayed': 1,
        'ParameterDependencePropagatesThroughAnotherMotionTimeRelay': 1,
        'FutureTimedTransitionsAreStillValidatedForIndependentParameterSupport': 1,
        'SelectedMenuExpressionRetainsItsStrictParameterCurveContract': 1,
        'GestureSmoothingFeedbackAndUnrelatedFacialConsumerKeepNativeBody': 4,
        'GestureSmoothingThatActuallyControlsBodyStillRequiresItsParameterProof': 1,
        'FeedbackWithUnprovedNativeDefaultContributionIsNotExempt': 3,
        'GestureSmoothingUnusedEndpointTangentsDoNotRequirePortableEncoding': 3,
        'IndependentNativeParametersAreNotLimitedByPortableMorphDomains': 4,
        'NativeParameterValidationStillRejectsAnActiveInteriorNaNTangent': 2,
    },
    'NeutralFallbackExportTests': {
        'UnresolvedBodyRestExportsWithIndependentOpenEyesAndAbsoluteEndpoints': 6,
    },
    'NeutralShapeEndpointTests': {
        'PreparedSnapshotRetainsIdentityMeshAndExplicitZeroWithoutChangingSource': 1,
        'InvalidAuthorIndexCannotAddressNewlyAppendedBlinkOrExpressionChannel': 2,
        'ExplicitEndpointsSupportNegativeZeroAndAboveHundred': 3,
        'ProfileWeightsAreAbsoluteSourceEndpointsAndMapEachRendererIndependently': 2,
    },
    'PreparedNeutralEligibilityTests': {
        'PreBuildGuardAllowsWeightChangesButStillProtectsDeformation': 1,
        'PreparedPathMappingCannotChooseBetweenAmbiguousOriginalOrCurrentPaths': 1,
    },
    'PreparedNeutralExportTests': {
        'IndependentLoopOnTheInfluencingSkinBoneKeepsPreparedGeometryAndAuthoredEndpoints': 1,
        'OneClickManualBlinkPreservesCoupledPreparedAppearanceAndAbsoluteClosure': 1,
        'OneClickKeepsSourceTrackingEndpointsAndPreparedRestAfterMarkerRemoval': 1,
        'AutomaticBlinkUsesFxOpenNeutralWhenSerializedUnifiedClosureIsFullyClosed': 2,
        'NdmfGeneratedShapeAndReboundFxUsePreparedMeshAndRendererPathInRealVrm': 8,
    },
    'NeutralShapePipelineTests': {
        'NeutralAndAbsoluteEndpointRoundTripWithLargeSharedMeshes': 4,
    },
    'NeutralShapeExportTests': {
        'FxDefaultOpeningShapeSurvivesExportWithoutAnExpressionMenu': 6,
    },
    'UnifiedExpressionExportTests': {
        'MissingBlinkRequiresUsableUnifiedRoute': 26,
        'ClampedNegativeRestKeepsNativeGeometryAndProvidesMovingUnifiedBlinkFallback': 2,
    },
    'MaSceneReferencePreparationTests': {
        'InstalledMaResolvesStaleDirectTargetAndMergesOnlyTheOwnedAvatar': 2,
        'OrdinaryExternalComponentReferencesRemainRejectedWithTheExactField': 2,
        'ActiveMaPathReferenceRetainsTheRequiredInactiveAuthoringBeforeCanonicalBuild': 1,
        'InstalledMaMergesAnInactiveNestedArmatureWithItsDeclaredTargets': 1,
    },
    'NdmfPreparationTests': {
        'ExportPreparationRunsBetweenCanonicalPhasesOnTheSameUnfinishedContext': 1,
        'PreparationFailureSkipsOptimizationAndStillFinishesAndCleansUp': 1,
        'OptimizationFailureStillFinishesAndCleansUp': 1,
        'UnresolvedFollowingTargetCanReachCanonicalResolutionOnTheOwnedCopy': 1,
        'ExplicitExclusionCanOmitInvalidInactiveAuthoringWithoutEditingTheSource': 1,
    },
    'DirectFxExpressionDiscoveryTests': {
        'CustomFxParameterDiscoversFaceWithoutAMenuOrFaceEmo': 1,
        'DescriptorFxIsAuthoritativeWhenRootAnimatorControllerIsNone': 1,
        'NestedMachineEntryCombinesItsGateAndItsExpressionCondition': 1,
        'NestedMachineEntryRetainsItsPrecedingStateGate': 1,
        'NestedEntryStateCycleTerminatesWithoutLosingAnIndependentExpression': 1,
        'NestedExitRouteRetainsItsInnerAndParentTransitionConditions': 2,
        'NestedExitFromEntryPreservesItsProvenNativeSelection': 1,
        'NestedExitBlockedByActiveAnyStateKeepsTheNativeReachabilityDiagnostic': 1,
        'RecursiveNestedExitRetainsEachLevelOfConditions': 1,
        'NestedExitCycleDoesNotSuppressAnIndependentNativeFace': 1,
        'DeepNestedExitPathReportsAnAtomicOptionalDiagnostic': 1,
        'ChainedStateTransitionsKeepThePrecedingParameterGate': 1,
        'OneDimensionalTreePublishesAuthoredSelectionsThroughNativeBlending': 1,
        'NestedTreeRetainsBothAuthoredControlValues': 1,
        'NestedTreesSharingAControlRetainInnerKnotsBeyondTheParentRange': 1,
        'UserSelectionAroundARuntimeControlledInnerTreeUsesTheFixedEnvironment': 1,
        'RuntimeParentKeepsInnerUserControlAtTheFixedEnvironment': 2,
        'GeneratedParentKeepsItsAuthoredValueWhileDiscoveringInnerUserControls': 2,
        'DirectTreeRetainsReadonlyWeightsWhileDiscoveringItsNestedUserControl': 1,
        'TwoDimensionalTreeUsesItsAuthoredCoordinates': 1,
        'DirectTreeUsesExplicitOneHotControlsInsteadOfFlattenedLeaves': 1,
        'StateEntryDriverKeepsItsCallbackDependentNativeLayerComposition': 1,
        'DriverOnlyRootControlKeepsCompleteNativeFaceWithoutSelectingItsInternalOutputs': 2,
        'ExplicitlyDeclaredDriverOutputRemainsAnAuthoredInput': 1,
        'AnimatorCurveRelayKeepsTheAuthoredRootControlAndNativeOverrideOutput': 2,
        'CurveOnlyStateCandidateUsesTheEffectiveOverrideBindings': 1,
        'ConjoinedFloatConditionsUseAValueInsideTheAuthoredRange': 1,
        'MutedAndImpossibleConditionsCannotCreateUnreachableCandidates': 1,
        'TriggerDrivenFaceKeepsAnExplicitCapabilityDiagnostic': 1,
        'InferredCandidateBudgetCannotRemoveAuthoredExpressionsOrPublishAPartialSubset': 1,
        'DeepConditionChainReportsAnAtomicOptionalDiscoveryDiagnostic': 1,
        'OverrideControllerAndFractionalNativeLayerKeepTheWholeFace': 1,
        'IncompatibleGraphReportsCandidateInsteadOfPublishingAnIsolatedClip': 3,
        'PriorityThatSelectsAnotherStateCannotMislabelTheCapturedFace': 1,
        'ExistingMenuOutcomeKeepsItsAuthoredLabelAndRecoveryDoesNotReintroduceMenuControls': 1,
        'RecoveryWithUnknownMenuInventoryCannotInferPotentiallyExcludedControls': 1,
        'GestureAutomaticBlinkAndTrackingChannelsAreNotFloodedWithGenericCandidates': 1,
        'InternalActivationAndAnimatorWrittenSignalsCannotBecomeIndependentUserSelections': 3,
        'InstalledMaDataOnlyMarkerPreservesNativeAndDirectExpressionSampling': 4,
        'InstalledMaMarkerCannotMakeAnUnknownCallbackSafe': 1,
        'NativeProcessedControllerCanRetainAFiniteTransformCurveWithNoTarget': 2,
        'TheSameTransformCurveOnALiveOrInactiveTargetStillRejectsTheNativeExpression': 2,
    },
    'MaReactiveExpressionExportTests': {
        'MixedMenuSetAndDeleteKeepsSetWithoutReapplyingDeletion': 1,
        'AuthoredActivationKeepsInactiveDependentShape': 2,
        'MergeMotionActivationKeepsNonMenuReactionThroughCanonicalBuild': 6,
        'MenuReactiveShapeSurvivesCanonicalBuildAndVrmReload': 6,
    },
    'InstalledNdmfNeutralExportTests': {
        'InstalledModularAvatarProcessesAnInactiveMergeAnimatorForPreparedFx': 1,
    },
}
INTEGRATION_CASES = {
    # The integration project must install the official MeshDeleterWithTexture
    # package. Synthetic sharedMesh replacement alone is not its integration
    # evidence, and an absent optional package must fail this explicit profile.
    'MeshDeleterIntegrationTests': {
        'DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport': 4,
    },
    'MaDeletionExportTests': {
        'InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings': 6,
    },
    'MaPermanentAppearanceExportTests': {
        'MergedPermanentPupilHideSurvivesVrmReloadBlinkAndMenuGeometry': 4,
    },
    'AppearancePreparationTests': {
        'PreparedRestAndMovedRendererKeepExpressionEndpointsWithoutDoubling': 2,
        'InstalledMaResolvesShapeMaterialAndVisibilityBeforeBaseShape': 2,
        'InstalledMeshCutterPreservesMorphsAndSourceMesh': 2,
        'ExcludedRulesCannotChangeRetainedAppearanceAndSimulatorOverridesStillMap': 2,
        'ExclusionsPreserveSimulatorMenuSelectionForAutomaticAndNamedParameters': 4,
    },
    'InstalledNdmfNeutralExportTests': {
        'InstalledModularAvatarRetargetsMovedRendererDefaultFxAndAuthoredEndpointInRealVrm': 2,
        'InstalledModularAvatarReplacementUsesFinalRendererKeyForNeutralWithoutAnAuthoredRoute': 1,
    },
    'InstalledAaoNeutralEndpointExportTests': {
        'MaskMergeAndTraceKeepFxNeutralAuthoredEndpointsTrackingAndDeferredBlink': 3,
    },
    'NdmfBlinkPreparationTests': {
        'InstalledAaoMergeKeepsTheSelectedRendererInCallbackFreeBlinkPreview': 1,
        'InstalledAaoMergeStillOptimizesAndRemapsBlinkForTheExportCallback': 1,
    },
    'PhysBoneSpringExportTests': {
        'InstalledAvatarOptimizerPreservesSpringMotionAndCollisionsAfterFullExport': 2,
    },
}
# Pin the new deletion combinations as well as their counts. A missing real
# tool or preview-disabled case cannot be replaced by a different parameter
# variant while retaining the same number of passing tests.
INTEGRATION_VARIANTS = {
    'MeshDeleterIntegrationTests': {
        'DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport': (
            'False,False', 'False,True', 'True,False', 'True,True'),
    },
    'MaDeletionExportTests': {
        'InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings': (
            'False,0', 'False,1', 'False,2', 'True,0', 'True,1', 'True,2'),
    },
    'MaPermanentAppearanceExportTests': {
        'MergedPermanentPupilHideSurvivesVrmReloadBlinkAndMenuGeometry': (
            'False,False', 'False,True', 'True,False', 'True,True'),
    },
}


def compatibility_counts(supported):
    counts = {'DependencyEnvironmentTests': 1, 'DependencyStartupTests': 1}
    if supported:
        counts.update(DependencyRoundTripTests=2, RendererSelectionTests=4, SkinnedMeshFallbackWeightTests=12)
    return counts


def required_regressions(supported, profile):
    if profile not in PROFILES:
        raise SystemExit('Unknown Unity validation profile: ' + profile)
    if profile != 'compatibility' and not supported:
        raise SystemExit('Exporter behavior/integration profiles require a supported real UniVRM environment')
    required = {
        'DependencyEnvironmentTests': {'ActualInstalledPackagesMatchRequestedTestEnvironment': 1},
        'DependencyStartupTests': {'MenuResolvesBackendWithoutInitializationRegistration': 1},
    }
    if supported:
        required['DependencyRoundTripTests'] = {'SupportedBackendPreservesMeshesMorphsAndMaterialBindingsOnReimport': 2}
    if profile != 'compatibility':
        required.update(BEHAVIOR_CASES)
    if profile == 'exporter-integration':
        for suite, methods in INTEGRATION_CASES.items():
            required[suite] = {**required.get(suite, {}), **methods}
    return required


def profile_filters(supported, profile):
    required = required_regressions(supported, profile)
    filters = []
    for suite in dict.fromkeys([*compatibility_counts(supported), *required]):
        if profile == 'exporter-behavior' and suite in INTEGRATION_CASES:
            # Shared suites have optional installed-tool cases. Select every
            # required behavior method, without silently accepting skipped ones.
            filters.extend(NAMESPACE + suite + '.' + method for method in required[suite])
        else:
            filters.append(NAMESPACE + suite)
    return filters


def validate_result(xml, supported, profile='compatibility'):
    required = required_regressions(supported, profile)
    tree = ET.parse(xml).getroot()
    cases = tree.findall('.//test-case')
    names = [c.get('fullname') for c in cases]
    if any(not name for name in names) or len(set(names)) != len(names):
        raise SystemExit('Missing or duplicate Unity test identities')
    for suite, methods in required.items():
        for method, expected_count in methods.items():
            name = NAMESPACE + suite + '.' + method
            found = [c for c in cases if c.get('classname') == NAMESPACE + suite and
                     (c.get('fullname') == name or c.get('fullname', '').startswith(name + '('))]
            if len(found) != expected_count or any(c.get('result') != 'Passed' for c in found):
                raise SystemExit('Required real-Unity regression did not pass (expected ' + str(expected_count) + ' cases): ' + name)
            variants = INTEGRATION_VARIANTS.get(suite, {}).get(method) if profile == 'exporter-integration' else None
            if variants is not None and {c.get('fullname') for c in found} != {name + '(' + args + ')' for args in variants}:
                raise SystemExit('Required real-Unity parameter combinations did not pass: ' + name)
    for suite, count in compatibility_counts(supported).items():
        found = [c for c in cases if c.get('classname') == 'VRVlog.LilToonExporter.Tests.' + suite]
        if len(found) != count:
            raise SystemExit('Required compatibility suite has missing or unexpected cases: ' + suite)
    allowed = {NAMESPACE + suite for suite in [*compatibility_counts(supported), *required]}
    if any(c.get('classname') not in allowed for c in cases):
        raise SystemExit('Unexpected test classes in Unity validation profile: ' + profile)
    if tree.get('result') != 'Passed' or any(c.get('result') != 'Passed' for c in cases):
        raise SystemExit('Failed or skipped real-Unity tests; see ' + str(xml))
    return cases


def source_identity(root):
    """Identify the runner checkout, not an unverified package in the test project."""
    root = root.resolve()
    identity = {'packageVersion': None, 'gitCommit': None, 'gitDirty': None}
    try:
        identity['packageVersion'] = json.loads((root / 'package.json').read_text(encoding='utf-8-sig'))['version']
    except (OSError, ValueError, KeyError):
        pass
    # Do not identify a source archive using an unrelated ancestor repository.
    def git(*args):
        result = subprocess.run(['git', '-c', 'safe.directory=' + str(root), '-C', str(root), *args],
                                capture_output=True, text=True, timeout=10)
        return result.stdout.strip() if result.returncode == 0 else None
    try:
        top = git('rev-parse', '--show-toplevel')
        if top is not None and Path(top).resolve() == root:
            commit = git('rev-parse', 'HEAD')
            if commit is not None and re.fullmatch(r'[0-9a-f]{40,64}', commit):
                identity['gitCommit'] = commit
                status = git('status', '--porcelain', '--untracked-files=normal')
                if status is not None:
                    identity['gitDirty'] = bool(status)
    except (OSError, subprocess.SubprocessError):
        pass
    return identity


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--expect-univrm', required=True)
    parser.add_argument('--expect-unity', default='2022.3.62f3')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--profile', choices=PROFILES, default='compatibility',
                        help='Compatibility only (default), generated exporter behavior, or behavior plus installed AAO/NDMF integration')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    config = json.loads((root / 'Compatibility/dependencies.json').read_text(encoding='utf-8'))
    supported = args.expect_univrm in config['uniVrm']['versions']
    filters = profile_filters(supported, args.profile)
    identity_before = source_identity(root)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    # Unique result names prevent an earlier successful XML from satisfying a failed run.
    stamp = str(time.time_ns())
    xml = output / (stamp + '.xml')
    log = output / (stamp + '.log')
    env = dict(os.environ, VRVLOG_TEST_UNIVRM=args.expect_univrm, VRVLOG_TEST_UNITY=args.expect_unity)
    command = [str(args.unity.resolve()), '-batchmode', '-projectPath', str(args.project.resolve()),
               '-runTests', '-testPlatform', 'EditMode', '-testFilter', ';'.join(filters),
               '-testResults', str(xml), '-logFile', str(log)]
    # GPU-backed editor is needed for real material/texture behavior; no -nographics.
    flags = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {}
    started = time.monotonic()
    result = subprocess.run(command, env=env, timeout=args.timeout, **flags)
    if result.returncode != 0 or not xml.exists():
        raise SystemExit('Unity failed or produced no test result. See ' + str(log))
    cases = validate_result(xml, supported, args.profile)
    identity_after = source_identity(root)
    if any(identity_before[key] != identity_after[key] for key in ('packageVersion', 'gitCommit')):
        raise SystemExit('Runner package version or Git commit changed during Unity validation')
    report = {'profile': args.profile, 'expectedUniVrm': args.expect_univrm, 'unity': args.expect_unity,
              'tests': len(cases), 'result': 'Passed', 'runnerSource': identity_before,
              'seconds': round(time.monotonic() - started), 'xml': xml.name, 'log': log.name}
    (output / (stamp + '.json')).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
