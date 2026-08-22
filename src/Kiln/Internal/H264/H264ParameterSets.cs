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

    /// <param name="width">Multiple of 16.</param>
    /// <param name="height">Multiple of 16.</param>
    /// <param name="maxNumRefFrames">max_num_ref_frames to signal (1..<see cref="MaxNumRefFrames"/>); must match the encoder's effective DPB usage.</param>
    public static byte[] WriteSpsRbsp(int width, int height, byte profileIdc, byte levelIdc, int maxNumRefFrames = MaxNumRefFrames)
    {
        if (profileIdc != 66)
        {
            throw new NotSupportedException(
                $"Only Baseline profile (profile_idc 66) is supported; got {profileIdc}. " +
                "Non-Baseline profiles require aligned constraint flags and additional SPS/PPS/slice " +
                "syntax that this encoder does not emit (see §7.3.2.1, Annex A).");
        }

        var bs = new H264RbspBitBuffer();
        var mbW = width / 16;
        var mbH = height / 16;

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
        bs.WriteBit(false); // frame_cropping_flag
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

    public static byte[] WritePpsRbsp(int picInitQpMinus26)
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
        bs.WriteBit(false); // constrained_intra_pred_flag
        bs.WriteBit(false); // redundant_pic_cnt_present_flag
        bs.WriteRbspTrailingBits();
        return bs.ToArray();
    }

    /// <summary>Whether <c>seq_parameter_set_data</c> includes <c>chroma_format_idc</c> and following fields (H.264 7.3.2.1).</summary>
    private static bool SpsIncludesChromaFormat(byte profileIdc) =>
        profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 134 or 135 or 139;
}
