namespace Kiln.Internal.H264;

/// <summary>SPS/PPS NAL RBSP writers (baseline, 4:2:0, frame_mbs_only, pic_order_cnt_type 2).</summary>
internal static class H264ParameterSets
{
    /// <summary>
    /// <c>log2_max_frame_num_minus4</c> written to the SPS (§7.3.2.1) and read by the slice
    /// header writer to determine <c>frame_num</c> bit width (§7.3.3). Single source of truth
    /// so SPS and slice writer always agree. Value 0 → frame_num field is 4 bits wide,
    /// supporting up to 16 distinct frame numbers before wrap (adequate for CBP Baseline
    /// streaming where IDR period is ≤ 16 frames).
    /// </summary>
    public const int Log2MaxFrameNumMinus4 = 0;

    /// <summary>
    /// <c>max_num_ref_frames</c> in SPS (§7.3.2.1). Encoder supports up to two stored reference pictures; signalling 2 matches the DPB depth.
    /// </summary>
    public const int MaxNumRefFrames = 2;

    /// <summary>
    /// Luma samples per crop unit for 4:2:0 (<c>ChromaArrayType == 1</c>):
    /// <c>CropUnitX = SubWidthC = 2</c>, <c>CropUnitY = SubHeightC · (2 − frame_mbs_only_flag) = 2</c>
    /// (§7.4.2.1.1, Table 6-1). Crop offsets move in 2-luma-sample units, so odd display extents
    /// are unrepresentable — callers must reject odd dimensions, not round them.
    /// </summary>
    public const int CropUnit = 2;

    /// <param name="codedWidth">Coded (padded) picture width — multiple of 16.</param>
    /// <param name="codedHeight">Coded (padded) picture height — multiple of 16.</param>
    /// <param name="maxNumRefFrames">max_num_ref_frames to signal (1..<see cref="MaxNumRefFrames"/>); must match the encoder's effective DPB usage.</param>
    /// <param name="displayWidth">
    /// Display width in luma samples; 0 (default) or equal to <paramref name="codedWidth"/> means no
    /// horizontal cropping. When smaller, the difference is signalled as <c>frame_crop_right_offset</c>
    /// (§7.3.2.1.1) in <see cref="CropUnit"/>-luma-sample units; left offset stays 0 so the MB grid
    /// stays aligned to the visible origin.
    /// </param>
    /// <param name="displayHeight">
    /// Display height in luma samples; 0 (default) or equal to <paramref name="codedHeight"/> means no
    /// vertical cropping. When smaller, the difference is signalled as <c>frame_crop_bottom_offset</c>
    /// (§7.3.2.1.1); top offset stays 0.
    /// </param>
    public static byte[] WriteSpsRbsp(
        int codedWidth, int codedHeight, byte profileIdc, byte levelIdc, int maxNumRefFrames = MaxNumRefFrames,
        int displayWidth = 0, int displayHeight = 0)
    {
        if (profileIdc != 66)
        {
            throw new NotSupportedException(
                $"Only Baseline profile (profile_idc 66) is supported; got {profileIdc}. " +
                "Non-Baseline profiles require aligned constraint flags and additional SPS/PPS/slice " +
                "syntax that this encoder does not emit (see §7.3.2.1, Annex A).");
        }

        if (displayWidth == 0)
        {
            displayWidth = codedWidth;
        }

        if (displayHeight == 0)
        {
            displayHeight = codedHeight;
        }

        if (displayWidth < 2 || displayWidth > codedWidth || (displayWidth & 1) != 0
            || displayHeight < 2 || displayHeight > codedHeight || (displayHeight & 1) != 0)
        {
            throw new ArgumentException(
                $"Display size {displayWidth}×{displayHeight} must be even and within the coded size " +
                $"{codedWidth}×{codedHeight}: 4:2:0 crop offsets move in CropUnitX=CropUnitY=2 luma-sample " +
                "units (§7.4.2.1.1, Table 6-1), so odd display extents are unrepresentable.");
        }

        var bs = new H264RbspBitBuffer();
        var mbW = codedWidth / 16;
        var mbH = codedHeight / 16;

        H264LevelLimits.ValidateFrameSize(levelIdc, mbW, mbH);

        bs.WriteBits(8, profileIdc);

        // constraint_set0..5, reserved_zero_2bits — constrained baseline: set 0 and 1.
        bs.WriteBit(true);  // constraint_set0_flag (Baseline)
        bs.WriteBit(true);  // constraint_set1_flag (CBP compatibility)
        bs.WriteBit(false); // constraint_set2_flag
        bs.WriteBit(false); // constraint_set3_flag
        bs.WriteBit(false); // constraint_set4_flag
        bs.WriteBit(false); // constraint_set5_flag
        bs.WriteBits(2, 0u); // reserved_zero_2bits

        bs.WriteBits(8, levelIdc);

        bs.WriteUe(0); // seq_parameter_set_id

        // chroma/bit depth/scaling — only for High / progressive High / CAVLC 4:4:4 / scalable /
        // Multiview / High throughput profiles (see Rec. ITU-T H.264 7.3.2.1); Baseline/66 omits these.
        if (SpsIncludesChromaFormat(profileIdc))
        {
            bs.WriteUe(1); // chroma_format_idc (4:2:0)
            bs.WriteUe(0); // bit_depth_luma_minus8
            bs.WriteUe(0); // bit_depth_chroma_minus8
            bs.WriteBit(false); // qpprime_y_zero_transform_bypass_flag
            bs.WriteBit(false); // seq_scaling_matrix_present_flag
        }

        bs.WriteUe(Log2MaxFrameNumMinus4); // log2_max_frame_num_minus4: frame_num is Log2MaxFrameNumMinus4+4 bits wide
        bs.WriteUe(2); // pic_order_cnt_type = 2
        bs.WriteUe((uint)Math.Clamp(maxNumRefFrames, 1, MaxNumRefFrames)); // max_num_ref_frames (ue(v), §7.3.2.1)
        bs.WriteBit(false); // gaps_in_frame_num_value_allowed_flag

        bs.WriteUe((uint)(mbW - 1)); // pic_width_in_mbs_minus1
        bs.WriteUe((uint)(mbH - 1)); // pic_height_in_map_units_minus1
        bs.WriteBit(true); // frame_mbs_only_flag
        bs.WriteBit(true); // direct_8x8_inference_flag (required when frame_mbs_only_flag is 1)

        // frame_cropping_flag + frame_crop_{left,right,top,bottom}_offset (§7.3.2.1.1). Cropping is
        // display-stage-only: the decoder's DPB and loop filter operate on the uncropped coded
        // picture (§7.4.2.1.1), so only the SPS changes when display < coded. Offsets are in
        // CropUnitX = CropUnitY = 2 luma-sample units for 4:2:0 (§7.4.2.1.1, Table 6-1).
        var cropRight = codedWidth - displayWidth;
        var cropBottom = codedHeight - displayHeight;
        if (cropRight != 0 || cropBottom != 0)
        {
            bs.WriteBit(true); // frame_cropping_flag
            bs.WriteUe(0); // frame_crop_left_offset — 0 keeps the MB grid aligned to the visible origin
            bs.WriteUe((uint)(cropRight / CropUnit)); // frame_crop_right_offset
            bs.WriteUe(0); // frame_crop_top_offset
            bs.WriteUe((uint)(cropBottom / CropUnit)); // frame_crop_bottom_offset
        }
        else
        {
            bs.WriteBit(false); // frame_cropping_flag
        }

        bs.WriteBit(true);  // vui_parameters_present_flag
        // vui_parameters() — H.264 Annex E
        bs.WriteBit(false); // aspect_ratio_info_present_flag
        bs.WriteBit(false); // overscan_info_present_flag
        bs.WriteBit(true);  // video_signal_type_present_flag
        bs.WriteBits(3, 5u); // video_format = 5 (Unspecified)
        bs.WriteBit(false); // video_full_range_flag = 0 (limited)
        bs.WriteBit(true);  // colour_description_present_flag
        bs.WriteBits(8, 6u); // colour_primaries = 6 (BT.601 NTSC)
        bs.WriteBits(8, 6u); // transfer_characteristics = 6 (BT.601)
        bs.WriteBits(8, 6u); // matrix_coefficients = 6 (BT.601)
        bs.WriteBit(false); // chroma_loc_info_present_flag
        bs.WriteBit(false); // timing_info_present_flag
        bs.WriteBit(false); // nal_hrd_parameters_present_flag
        bs.WriteBit(false); // vcl_hrd_parameters_present_flag
        bs.WriteBit(false); // pic_struct_present_flag
        bs.WriteBit(false); // bitstream_restriction_flag

        bs.WriteRbspTrailingBits();
        return bs.ToArray();
    }

    /// <param name="picInitQpMinus26"><c>pic_init_qp_minus26</c> (§7.3.2.2).</param>
    /// <param name="constrainedIntraPred">
    /// <c>constrained_intra_pred_flag</c> (§7.4.2.2): when 1, intra prediction in P slices treats
    /// inter-coded neighbouring macroblocks as unavailable (§8.3.1.1, §8.3.1.2, §8.3.2, §8.3.4) so
    /// intra macroblocks never inherit inter-predicted content — the property gradual intra refresh
    /// relies on. The encoder's own intra paths must mirror the flag (see
    /// <c>H264BaselineSliceEncoder</c>); emitting it without doing so desynchronises every decoder.
    /// </param>
    public static byte[] WritePpsRbsp(int picInitQpMinus26, bool constrainedIntraPred = false)
    {
        var bs = new H264RbspBitBuffer();
        bs.WriteUe(0); // pic_parameter_set_id
        bs.WriteUe(0); // seq_parameter_set_id
        bs.WriteBit(false); // entropy_coding_mode_flag — CAVLC
        bs.WriteBit(false); // bottom_field_pic_order_in_frame_present_flag
        bs.WriteUe(0); // num_slice_groups_minus1
        bs.WriteUe(0); // num_ref_idx_l0_default_active_minus1
        bs.WriteUe(0); // num_ref_idx_l1_default_active_minus1
        bs.WriteBit(false); // weighted_pred_flag
        bs.WriteBits(2, 0u); // weighted_bipred_idc
        bs.WriteSe(picInitQpMinus26);
        bs.WriteSe(0); // pic_init_qs_minus26
        bs.WriteSe(0); // chroma_qp_index_offset
        bs.WriteBit(true); // deblocking_filter_control_present_flag
        bs.WriteBit(constrainedIntraPred); // constrained_intra_pred_flag
        bs.WriteBit(false); // redundant_pic_cnt_present_flag
        bs.WriteRbspTrailingBits();
        return bs.ToArray();
    }

    /// <summary>
    /// Recovery point SEI message RBSP (§D.1.8 syntax, §D.2.8 semantics): tells a decoder that
    /// starts decoding at this access unit that its output is correct in content once
    /// <paramref name="recoveryFrameCnt"/> further frames (in <c>frame_num</c> counting order) have
    /// been decoded. Emitted at the first frame of a gradual intra refresh wave so a mid-stream
    /// joiner knows when the sweep completes. <c>exact_match_flag</c> is 1 — the wave's motion
    /// vector restrictions and <c>constrained_intra_pred_flag</c> make post-recovery reconstruction
    /// bit-exact, not approximate. <c>broken_link_flag</c> 0 and <c>changing_slice_group_idc</c> 0
    /// (no slice groups in this encoder).
    /// </summary>
    public static byte[] WriteRecoveryPointSeiRbsp(int recoveryFrameCnt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recoveryFrameCnt);

        // sei_payload( 6, size ) body, built first because sei_message needs its byte size.
        var payload = new H264RbspBitBuffer();
        payload.WriteUe((uint)recoveryFrameCnt); // recovery_frame_cnt
        payload.WriteBit(true);  // exact_match_flag
        payload.WriteBit(false); // broken_link_flag
        payload.WriteBits(2, 0u); // changing_slice_group_idc
        // §D.1: a payload that is not byte-aligned ends with bit_equal_to_one plus alignment
        // zero bits — the same pattern as rbsp_trailing_bits, so reuse that writer.
        payload.WriteRbspTrailingBits();
        var payloadBytes = payload.ToArray();

        var bs = new H264RbspBitBuffer();
        bs.WriteBits(8, 6u); // last_payload_type_byte = 6 (recovery_point); < 255 so single byte
        bs.WriteBits(8, (uint)payloadBytes.Length); // last_payload_size_byte; < 255 for any frame count
        foreach (var b in payloadBytes)
        {
            bs.WriteBits(8, b);
        }

        bs.WriteRbspTrailingBits();
        return bs.ToArray();
    }

    /// <summary>Whether <c>seq_parameter_set_data</c> includes <c>chroma_format_idc</c> and following fields (H.264 7.3.2.1).</summary>
    private static bool SpsIncludesChromaFormat(byte profileIdc) =>
        profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 134 or 135 or 139;
}
